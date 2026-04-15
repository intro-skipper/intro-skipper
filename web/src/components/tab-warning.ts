import { el } from "./dom.ts";

/** Shared warning banner used at the top of config tabs. */
export function tabWarning(text: string): HTMLElement {
  const warning = el("div", { className: "tab-warning", role: "status" });
  const icon = el("span", { className: "tab-warning-icon" });
  icon.textContent = "\u26a0";
  const msg = el("span");
  msg.textContent = text;
  warning.append(icon, msg);
  return warning;
}
