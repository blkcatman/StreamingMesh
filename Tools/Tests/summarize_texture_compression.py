"""Validate captured GPU probe logs and summarize the isolated 8K load comparison."""
import argparse
import json
from pathlib import Path


def read(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def events(logs, prefix):
    return [json.loads(item["message"][len(prefix):]) for item in logs if item["message"].startswith(prefix)]


def validate(result):
    assert result["completed"] and result["failures"] == 0, "Probe did not pass"
    for item in result["results"]:
        assert item["status"] in ("PASS", "UNSUPPORTED", "CONTROL_PASS")
        if item["status"] == "PASS":
            assert item["supported"] and item["compressed"] and not item["readable"]
            assert item["mipCount"] == 1 and item["meanError"] <= 8 and item["maxError"] <= 64
        if item["status"] == "CONTROL_PASS":
            assert not item["compressed"] and not item["readable"] and item["mipCount"] == 1
            assert item["meanError"] <= 1 and item["maxError"] <= 4


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--logs", type=Path, default=Path("Logs"))
    args = parser.parse_args()
    native = read(args.logs / "TextureCompression-native-results.json")
    matrix = read(args.logs / "TextureCompression-WebGPU-matrix-results.json")
    validate(native)
    validate(matrix)
    assert len(matrix["results"]) == 14, "Expected seven formats in sRGB and linear"
    matrix_logs = read(args.logs / "TextureCompression-WebGPU-matrix-console.json")
    assert not any(item["level"] == "error" for item in matrix_logs)
    exports = []
    for line in (args.logs / "TextureCompression-verification.log").read_text(encoding="utf-8-sig").splitlines():
        if line.startswith("STM_TEX_EXPORT "):
            exports.append(json.loads(line[len("STM_TEX_EXPORT "):]))
    assert len(exports) == 15, "Expected 14 fixtures and one 8K BC7 export"
    assert len({entry["name"] for entry in exports}) == 15
    assert {entry["name"] for entry in exports if entry["width"] == 48} == {item["name"] for item in matrix["results"]}
    summary = {"native": native, "webMatrix": matrix, "exports": exports, "loads": {}}
    for mode in ("bc7", "bc7-native", "png"):
        result = read(args.logs / f"TextureCompression-WebGPU-{mode}-results.json")
        validate(result)
        assert len(result["results"]) == 1 and result["results"][0]["width"] == 8192
        assert result["results"][0]["height"] == 8192
        logs = read(args.logs / f"TextureCompression-WebGPU-{mode}-console.json")
        assert not any(item["level"] == "error" for item in logs), "Browser console contains errors"
        snapshots = events(logs, "STM_TEX_MEM ")
        if mode == "bc7-native":
            assert not any(item["stage"] == "body:after-managed-copy" for item in snapshots)
        grows = events(logs, "STM_GROW ")
        metrics = events(logs, "STM_METRICS ")
        settled = next(item for item in snapshots if item["stage"] == "body:settled-after-gc")
        capacity = max([item["wasmCapacity"] for item in snapshots] + [item["newSize"] for item in grows] +
                       [item["totalWASMHeapSize"] for item in metrics])
        summary["loads"][mode] = {"result": result["results"][0], "capacityMax": capacity,
            "allocatedCheckpointMax": max(item["wasmAllocated"] for item in snapshots),
            "settledAfterGc": settled, "snapshots": snapshots, "growth": grows}
    summary["capacityReductionPercent"] = (1 - summary["loads"]["bc7-native"]["capacityMax"] /
                                           summary["loads"]["png"]["capacityMax"]) * 100
    output = args.logs / "TextureCompression-summary.json"
    output.write_text(json.dumps(summary, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(f"PASS exports={len(exports)}, native failures=0, WebGPU failures=0")
    for mode, load in summary["loads"].items():
        print(f"{mode}: capacity={load['capacityMax'] / 1048576:.1f} MiB, "
              f"settled WASM allocated={load['settledAfterGc']['wasmAllocated'] / 1048576:.1f} MiB, "
              f"settled managed used={load['settledAfterGc']['monoUsed'] / 1048576:.1f} MiB")
    print(f"Capacity reduction: {summary['capacityReductionPercent']:.1f}%")
    print(f"Summary: {output}")


if __name__ == "__main__":
    main()
