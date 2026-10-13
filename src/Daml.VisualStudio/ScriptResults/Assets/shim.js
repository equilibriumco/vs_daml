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
})();
