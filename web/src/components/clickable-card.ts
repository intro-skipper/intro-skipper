import { el } from "./dom.ts";

export type ClickableCardOptions = {
  title: string;
  subtitle?: string;
  onClick: () => void;
};

export type ClickableCardResult = {
  container: HTMLElement;
  subtitleEl: HTMLElement | null;
};

/**
 * Builds a card-like button used for library and show entries
 * in the timestamp browser.
 */
export function clickableCard(opts: ClickableCardOptions): ClickableCardResult {
  const card = el("button", {
    className: "ts-episode-card ts-episode-card-button",
    type: "button",
  });
  const info = el("div", { className: "ts-episode-info" });
  const header = el("div", { className: "ts-episode-header" });

  let subtitleEl: HTMLElement | null = null;
  header.append(el("span", { className: "ts-episode-name" }, opts.title));
  if (opts.subtitle) {
    subtitleEl = el("span", { className: "ts-episode-runtime" }, opts.subtitle);
    header.append(subtitleEl);
  }

  info.append(header);
  card.append(info);

  card.addEventListener("click", opts.onClick);

  return { container: card, subtitleEl };
}
