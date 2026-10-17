import type { Tab } from "../types.ts";
import { actionButton } from "../components/action-button.ts";
import { bindStatusMessage } from "../components/async-feedback.ts";
import { el } from "../components/dom.ts";
import * as api from "../store/api.ts";

const MODES: readonly api.RemoteAnalysisMode[] = ["Introduction", "Credits", "Recap", "Preview", "Commercial"];
const LOCAL_BUILT_IN_MODES = MODES;

function displayBytes(bytes: number | null): string {
    return bytes === null ? "Unavailable" : `${(bytes / 1024 / 1024 / 1024).toFixed(1)} GB`;
}

function isOnline(node: api.RemoteNodeInfo): boolean {
    if (node.isLocal) return true;
    if (!node.lastSeenUtc) return false;
    const age = Date.now() - Date.parse(node.lastSeenUtc);
    return Number.isFinite(age) && age >= 0 && age <= 90_000;
}

function initialPolicy(node: api.RemoteNodeInfo, libraries: readonly api.RemoteLibrary[]): api.RemoteNodePolicy {
    if (node.policy) return node.policy;
    return {
        libraryIds: node.isLocal ? libraries.map((library) => library.id) : node.libraryIds,
        modes: node.modes.length ? node.modes : [...(node.isLocal ? LOCAL_BUILT_IN_MODES : MODES)],
        creditsMethod: node.isLocal ? "IntroSkipper" : node.creditsMethod ?? "IntroSkipper",
        useGpu: false,
        concurrentFiles: 2,
        pathMappings: [],
        automaticImports: node.automaticImports ?? false,
        leaseMinutes: node.leaseMinutes ?? 30,
    };
}

function check(label: string, checked: boolean, disabled = false): { label: HTMLLabelElement; input: HTMLInputElement } {
    const input = document.createElement("input");
    input.type = "checkbox";
    input.checked = checked;
    input.disabled = disabled;
    const wrapper = el("label", { className: "check" });
    wrapper.append(input, document.createTextNode(label));
    return { label: wrapper, input };
}

function hardware(node: api.RemoteNodeInfo): HTMLElement {
    const info = node.runtime?.hardware;
    const gpus = info?.gpus.length
        ? info.gpus.map((gpu) => `${gpu.name}${gpu.driver ? ` (${gpu.driver})` : ""}`).join("; ")
        : "Unavailable";
    return el("div", { className: "node-hardware" },
        el("strong", {}, isOnline(node) ? "Online" : "Offline"),
        el("p", {}, `Version: ${node.runtime?.version || "Unavailable"}`),
        el("p", {}, `OS: ${info?.operatingSystem || "Unavailable"}`),
        el("p", {}, `CPU: ${info?.cpuName || "Unavailable"}`),
        el("p", {}, `Cores: ${info?.logicalProcessors ? String(info.logicalProcessors) : "Unavailable"}`),
        el("p", {}, `Memory: ${displayBytes(info?.memoryBytes ?? null)}`),
        el("p", {}, `GPU: ${gpus}`));
}

function nodeCard(node: api.RemoteNodeInfo, libraries: readonly api.RemoteLibrary[], signal: AbortSignal, reload: () => Promise<void>): HTMLElement {
    const policy = initialPolicy(node, libraries);
    const section = el("section", { className: "node-card" });
    const status = el("p", { className: "status-message", role: "status", "aria-live": "polite" });
    const feedback = bindStatusMessage(status);
    const enabled = check("Enabled", node.enabled);
    const libraryControls = libraries.map((library) => {
        const control = check(library.name, policy.libraryIds.includes(library.id));
        return { id: library.id, input: control.input, element: el("div", { className: "library" }, control.label, ...(library.paths.length ? [el("small", {}, library.paths.join(" · "))] : [])) };
    });
    const supported = new Set(node.runtime?.supportedModes ?? (node.isLocal ? LOCAL_BUILT_IN_MODES : MODES));
    const modeControls = MODES.map((mode) => {
        const control = check(mode === "Introduction" ? "Introductions" : mode, policy.modes.includes(mode), !supported.has(mode));
        return { mode, input: control.input, element: control.label };
    });
    const method = document.createElement("select");
    for (const [value, label] of [["IntroSkipper", "Original Intro Skipper"], ["TextAware", "Text-aware"]]) {
        const option = document.createElement("option"); option.value = value; option.textContent = label; method.append(option);
    }
    method.value = policy.creditsMethod;
    const recipes = node.runtime?.creditsRecipes;
    method.options[0].disabled = !node.isLocal && recipes !== undefined && !recipes.some((recipe) => recipe.method === "IntroSkipper");
    method.options[1].disabled = node.isLocal || (recipes !== undefined && !recipes.some((recipe) => recipe.method === "TextAware"));
    const gpu = check("Use GPU", policy.useGpu);
    const workers = document.createElement("select");
    for (const value of [1, 2, 4]) { const option = document.createElement("option"); option.value = String(value); option.textContent = String(value); workers.append(option); }
    workers.value = String(policy.concurrentFiles);
    const automaticImports = check("Use complete scan results automatically", policy.automaticImports);
    const mappings = el("div");
    const mappingRows: Array<{ row: HTMLElement; server: HTMLInputElement; worker: HTMLInputElement; mapping: api.PathMapping }> = [];
    const addMapping = (mapping: api.PathMapping = { id: "", serverPrefix: "", workerPrefix: "", caseSensitive: null }) => {
        const server = document.createElement("input"); server.type = "text"; server.value = mapping.serverPrefix;
        const worker = document.createElement("input"); worker.type = "text"; worker.value = mapping.workerPrefix;
        const remove = el("button", { type: "button" }, "Remove mapping");
        const row = el("div", { className: "side-by-side" }, el("label", {}, "Jellyfin path", server), el("label", {}, "Node path", worker), remove);
        remove.addEventListener("click", () => { const index = mappingRows.findIndex((candidate) => candidate.row === row); if (index >= 0) mappingRows.splice(index, 1); row.remove(); }, { signal });
        mappingRows.push({ row, server, worker, mapping }); mappings.append(row);
    };
    policy.pathMappings.forEach(addMapping);
    const addPath = actionButton("Add path mapping", () => addMapping());
    const updateGpu = () => {
        const selected = method.value as api.CreditsDetectionMethod;
        gpu.input.disabled = node.isLocal || (recipes !== undefined && !recipes.some((recipe) => recipe.method === selected && recipe.useGpu));
        if (gpu.input.disabled) gpu.input.checked = false;
    };
    method.addEventListener("change", updateGpu, { signal }); updateGpu();
    const save = actionButton("Save node", async () => {
        const concurrentFiles = Number(workers.value);
        if (![1, 2, 4].includes(concurrentFiles)) { feedback.show("Choose 1, 2, or 4 workers."); return; }
        const pathMappings = mappingRows.map(({ mapping, server, worker }) => ({ ...mapping, serverPrefix: server.value.trim(), workerPrefix: worker.value.trim() }));
        if (pathMappings.some((mapping) => !mapping.serverPrefix || !mapping.workerPrefix)) { feedback.show("Each path mapping needs both a Jellyfin path and a node path."); return; }
        save.setAttribute("disabled", "");
        try {
            const result = await api.saveRemoteNodePolicy(node.nodeId, {
                libraryIds: libraryControls.filter((control) => control.input.checked).map((control) => control.id),
                modes: modeControls.filter((control) => control.input.checked).map((control) => control.mode),
                creditsMethod: node.isLocal ? "IntroSkipper" : method.value as api.CreditsDetectionMethod, useGpu: !node.isLocal && gpu.input.checked, concurrentFiles,
                pathMappings: node.isLocal ? [] : pathMappings, automaticImports: automaticImports.input.checked, leaseMinutes: policy.leaseMinutes,
            }, enabled.input.checked, signal);
            if (!result.ok) { feedback.show(result.error); return; }
            await reload();
        } catch (error) { if (!signal.aborted) feedback.show(error instanceof Error ? error.message : "Could not save node policy."); }
        finally { if (!signal.aborted) save.removeAttribute("disabled"); }
    });
    const fields: Node[] = [
        el("fieldset", {}, el("legend", {}, "Libraries"), ...libraryControls.map((control) => control.element)),
        el("fieldset", {}, el("legend", {}, "Analysis types"), ...modeControls.map((control) => control.element)),
        ...(!node.isLocal ? [el("label", {}, "Credits method", method), gpu.label] : []),
        el("p", { className: "field-description" }, node.isLocal ? "Uses the scanner methods built into Intro Skipper." : "GPU accelerates video decoding. Text-aware credits also use GPU text detection. Resizing and colour conversion stay on the CPU."),
        el("label", {}, "Parallel workers", workers),
    ];
    if (node.isLocal) fields.push(el("p", {}, "This local node uses Jellyfin paths directly; path mappings are unavailable."));
    else fields.push(automaticImports.label, el("h3", {}, "Path variables"), el("p", {}, "Map Jellyfin paths to paths available to this node."), mappings, addPath);
    section.append(el("h2", {}, node.name), el("p", {}, node.isLocal ? "Local node" : "Remote node"), hardware(node), enabled.label, ...fields, save, status);
    return section;
}

export const nodesTab: Tab = {
    id: "nodes", label: "Nodes",
    render(container, signal) {
        const content = el("div"); const status = el("p", { role: "status", "aria-live": "polite" });
        const load = async () => {
            status.replaceChildren("Loading nodes…");
            try {
                const [nodes, libraries] = await Promise.all([api.getRemoteNodes(signal), api.getRemoteLibraries(signal)]);
                if (!nodes.ok) { status.replaceChildren(`Could not load nodes: ${nodes.error}`); return; }
                if (!libraries.ok) { status.replaceChildren(`Could not load libraries: ${libraries.error}`); return; }
                content.replaceChildren(...(nodes.data ?? []).map((node) => nodeCard(node, libraries.data ?? [], signal, load))); status.replaceChildren();
            } catch (error) { if (!signal.aborted) status.replaceChildren(error instanceof Error ? error.message : "Could not load nodes."); }
        };
        container.append(el("h1", {}, "Nodes"), el("p", {}, "Configure the local and remote scanners that may analyze this server's libraries."), actionButton("Refresh nodes", load), status, content);
        void load();
    },
};
