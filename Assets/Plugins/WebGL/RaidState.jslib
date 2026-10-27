mergeInto(LibraryManager.library, {
  NectorialRaidReportState: function (jsonPointer) {
    var json = UTF8ToString(jsonPointer);
    if (typeof window !== "undefined" && window.__nectorialRaid && window.__nectorialRaid.receiveState) {
      window.__nectorialRaid.receiveState(json);
    }
  }
});
