// wterm PoC 用の最小モジュール。CDN (esm.sh) から @wterm/dom をロードし、
// Blazor から呼び出せる init / write / dispose を公開する。
import { WTerm } from "https://esm.sh/@wterm/dom";

let term = null;

export async function init(elementId) {
    if (term) {
        console.warn("[wterm-poc] already initialized");
        return;
    }
    const el = document.getElementById(elementId);
    if (!el) {
        console.error("[wterm-poc] element not found:", elementId);
        return;
    }
    term = new WTerm(el, {
        onData(data) {
            console.log("[wterm-poc] onData:", JSON.stringify(data));
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
    if (term?.dispose) {
        try { term.dispose(); } catch (e) { console.error("[wterm-poc] dispose error:", e); }
    }
    term = null;
}
