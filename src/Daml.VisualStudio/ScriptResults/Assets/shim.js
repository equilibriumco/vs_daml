// What webview.js expects from VS Code, mapped onto WebView2. Loaded before webview.js.
(function () {
  const host = window.chrome && window.chrome.webview;
  let state;
  window.acquireVsCodeApi = function () {
    return {
      postMessage: function (message) { if (host) { host.postMessage(message); } },
      getState: function () { return state; },
      setState: function (value) { state = value; return value; }
    };
  };
  // Host messages arrive on chrome.webview; webview.js listens on window, as it does in VS Code.
  if (host) {
    host.addEventListener('message', function (e) {
      window.dispatchEvent(new MessageEvent('message', { data: e.data }));
    });
  }

  // The page is made for the mouse. The jumps between contracts are spans with an onclick, so
  // they become links the keyboard reaches; the disclosure cells say S, O or W and keep the word
  // in a tooltip shown only on hover, so a screen reader is given the word instead.
  document.addEventListener('DOMContentLoaded', function () {
    document.querySelectorAll('.da-hl-link').forEach(function (jump) {
      jump.setAttribute('tabindex', '0');
      jump.setAttribute('role', 'link');
    });
    document.querySelectorAll('td.disclosure').forEach(function (cell) {
      const tip = cell.querySelector('.tooltiptext');
      if (tip && tip.textContent) {
        cell.setAttribute('aria-label', tip.textContent);
      }
    });
  });

  document.addEventListener('keydown', function (e) {
    const target = e.target;
    if ((e.key === 'Enter' || e.key === ' ') && target && target.classList && target.classList.contains('da-hl-link')) {
      e.preventDefault();
      target.click();
    }
  });

  // A jump only scrolls; the focus follows it, so Tab carries on from the contract jumped to.
  window.addEventListener('hashchange', function () {
    let id;
    try {
      id = decodeURIComponent(location.hash.slice(1));
    } catch (_) {
      return;
    }
    const landing = id && document.getElementById(id);
    if (landing) {
      if (!landing.hasAttribute('tabindex')) {
        landing.setAttribute('tabindex', '-1');
      }
      landing.focus();
    }
  });
})();
