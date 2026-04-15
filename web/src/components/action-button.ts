import { el } from "./dom.ts";

export function actionButton(label: string, onClick: () => Promise<void> | void): HTMLElement {
  const button = el(
    "button",
    {
      className: "action-button raised block",
    },
    label,
  );

  button.addEventListener("click", async () => {
    button.disabled = true;
    try {
      await onClick();
    } finally {
      button.disabled = false;
    }
  });

  return button;
}
