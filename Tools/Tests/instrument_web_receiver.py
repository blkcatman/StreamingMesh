"""Add temporary Unity WASM metrics to a built index.html for browser verification."""
import argparse
from pathlib import Path
parser = argparse.ArgumentParser()
parser.add_argument("index", type=Path)
args = parser.parse_args()
source = args.index.read_text(encoding="utf-8-sig")
needle = ".then((unityInstance) => {"
if source.count(needle) != 1:
    raise SystemExit("Expected one Unity instance callback")
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
args.index.write_text(source.replace(needle, needle+probe), encoding="utf-8")
