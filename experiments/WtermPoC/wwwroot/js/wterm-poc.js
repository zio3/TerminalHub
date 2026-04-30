// wterm PoC 用の最小モジュール。CDN (esm.sh) から @wterm/dom をロードし、
// Blazor から呼び出せる init / write / dispose を公開する。
import { WTerm } from "https://esm.sh/@wterm/dom";

let term = null;
let dotnetRef = null;

export async function init(elementId, dotnet) {
    if (term) {
        console.warn("[wterm-poc] already initialized");
        return;
    }
    const el = document.getElementById(elementId);
    if (!el) {
        console.error("[wterm-poc] element not found:", elementId);
        return;
    }
    dotnetRef = dotnet;
    term = new WTerm(el, {
        onData(data) {
            if (dotnetRef) {
                dotnetRef.invokeMethodAsync("OnTerminalData", data).catch(e =>
                    console.error("[wterm-poc] OnTerminalData failed:", e));
            }
        },
        onResize(cols, rows) {
            console.log("[wterm-poc] onResize", cols, rows);
            if (dotnetRef) {
                dotnetRef.invokeMethodAsync("OnTerminalResize", cols, rows).catch(e =>
                    console.error("[wterm-poc] OnTerminalResize failed:", e));
            }
        },
    });
    await term.init();
    term.write("wterm PoC ready\r\n");
}

export function write(text) {
    if (!term) {
        console.warn("[wterm-poc] not initialized");
        return;
    }
    term.write(text);
}

export function dispose() {
    if (term?.destroy) {
        try { term.destroy(); } catch (e) { console.error("[wterm-poc] destroy error:", e); }
    }
    term = null;
    dotnetRef = null;
}
