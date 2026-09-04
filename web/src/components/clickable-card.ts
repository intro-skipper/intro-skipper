import { el } from "./dom.ts";

/**
 * Builds a card-like button used for library and show entries
 * in the timestamp browser. The subtitle may be an element the caller updates later.
 */
export function clickableCard(opts: {
    title: string;
    subtitle?: string | Node;
    onClick: () => void;
}): HTMLElement {
    const card = el("button", {
        className: "ts-episode-card ts-episode-card-button",
        type: "button",
    });
    const info = el("div", { className: "ts-episode-info" });
    const header = el("div", { className: "ts-episode-header" });

    header.append(el("span", { className: "ts-episode-name" }, opts.title));
    if (typeof opts.subtitle === "string") {
        header.append(el("span", { className: "ts-episode-runtime" }, opts.subtitle));
    } else if (opts.subtitle) {
        header.append(opts.subtitle);
    }

    info.append(header);
    card.append(info);

    card.addEventListener("click", opts.onClick);

    return card;
}
