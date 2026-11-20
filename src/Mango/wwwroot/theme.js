(() => {
  const root = document.documentElement;
  const key = "mango.theme";
  try { if (localStorage.getItem(key) === "light") root.dataset.theme = "light"; } catch {}
  document.addEventListener("DOMContentLoaded", () => {
    const button = document.querySelector("#theme-toggle");
    function render() {
      const light = root.dataset.theme === "light";
      button.title = light ? "Switch to dark mode" : "Switch to light mode";
      button.setAttribute("aria-label", button.title);
      button.querySelector("use").setAttribute("href", light ? "/glyphs.svg#moon" : "/glyphs.svg#sun");
    }
    button.addEventListener("click", () => {
      root.dataset.theme = root.dataset.theme === "light" ? "dark" : "light";
      try { localStorage.setItem(key, root.dataset.theme); } catch {}
      render();
    });
    render();
  });
})();
