"""Add temporary Unity WASM metrics to a built index.html for browser verification."""
import argparse
from pathlib import Path
parser = argparse.ArgumentParser()
parser.add_argument("index", type=Path)
parser.add_argument("--trace-growth", action="store_true", help="Trace Emscripten memory growth in the generated framework (uncompressed verification builds only)")
args = parser.parse_args()
source = args.index.read_text(encoding="utf-8-sig")
if "stm-memory-diagnostic" in source:
    raise SystemExit("Index is already instrumented; rebuild before instrumenting again")
needle = ".then((unityInstance) => {"
if source.count(needle) != 1:
    raise SystemExit("Expected one Unity instance callback")
if args.trace_growth:
    frameworks = list((args.index.parent / "Build").glob("*.framework.js"))
    if len(frameworks) != 1:
        raise SystemExit("Expected one uncompressed generated framework.js")
    framework = frameworks[0]
    code = framework.read_text(encoding="utf-8-sig")
    needle_growth = "if(replacement){return true}"
    replacement_growth = '''if(replacement){console.log("STM_GROW "+JSON.stringify({seconds:performance.now()/1000,stage:Module.StreamingMeshMemoryStage||"engine/startup",oldSize:oldSize,requestedSize:requestedSize,newSize:newSize,stack:(new Error()).stack.split("\\n").slice(0,12).join("\\n")}));return true;}'''
    if code.count(needle_growth) != 1:
        raise SystemExit("Unsupported generated heap growth implementation; framework was not changed")
    framework.write_bytes(code.replace(needle_growth, replacement_growth).encode("utf-8"))
probe = r'''
                const diagnostic = document.createElement("pre");
                diagnostic.id = "stm-memory-diagnostic";
                diagnostic.style.cssText = "position:fixed;right:4px;bottom:4px;max-width:620px;max-height:100px;overflow:auto;background:#000b;color:#fff;font:11px monospace;z-index:9999";
                document.body.appendChild(diagnostic);
                const started = performance.now();
                const samples = [];
                setInterval(() => {
                  const audio = document.querySelector("audio");
                  const sample = Object.assign({seconds:Math.round((performance.now()-started)/1000)}, unityInstance.GetMetricsInfo(),
                    {audioTime:audio ? audio.currentTime : null, audioPaused:audio ? audio.paused : null});
                  samples.push(sample);
                  diagnostic.textContent = JSON.stringify(samples);
                  console.log("STM_METRICS " + JSON.stringify(sample));
                }, 2000);
'''
args.index.write_bytes(source.replace(needle, needle+probe).encode("utf-8"))
print("Instrumented " + str(args.index) + (" with heap growth tracing" if args.trace_growth else ""))
