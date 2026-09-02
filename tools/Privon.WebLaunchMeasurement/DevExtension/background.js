// PRIVON dev-only measurement extension (Gate E5B). Its ONLY purpose is to connect to the
// com.privon.devmeasure native messaging host so that host's own process can observe and record
// the real launch shape Chrome/Edge actually supplies. This extension never reads page content,
// the DOM, tabs, or any URL other than its own extension origin, never requests any permission
// beyond "nativeMessaging", and never sends anything anywhere over the network.

try {
  const port = chrome.runtime.connectNative("com.privon.devmeasure");

  port.onDisconnect.addListener(() => {
    if (chrome.runtime.lastError) {
      console.log("[privon-devmeasure] native host disconnected:", chrome.runtime.lastError.message);
    } else {
      console.log("[privon-devmeasure] native host disconnected (expected: it exits immediately after recording evidence).");
    }
  });
} catch (err) {
  console.log("[privon-devmeasure] connectNative threw:", err);
}
