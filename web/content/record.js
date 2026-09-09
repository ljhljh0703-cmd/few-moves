(function () {
  "use strict";
  var rawHash = typeof location === "undefined" ? "" : (location.hash || "").slice(1);
  var recordValue = rawHash.indexOf("record=") === 0 ? rawHash.slice(7) : "";
  var validRecord = recordValue.length > 0 && recordValue.length <= 4096;
  var badge = document.querySelector("[data-record-badge]");
  var recordStatus = document.querySelector("[data-record-status]");
  var recordButton = document.querySelector("[data-record-share]");
  var fallback = document.querySelector("[data-record-fallback]");
  var recordLink = typeof location === "undefined" ? "" : location.href;
  function showFallback() { if (!fallback) return; fallback.hidden = false; fallback.value = recordLink; fallback.focus(); fallback.select(); if (recordStatus) recordStatus.textContent = "링크를 선택했습니다. 복사해 공유하세요."; }
  function shareRecord() { if (!recordLink) return; if (navigator.clipboard && typeof navigator.clipboard.writeText === "function") navigator.clipboard.writeText(recordLink).then(function () { if (recordStatus) recordStatus.textContent = "기록 링크를 복사했습니다."; }).catch(showFallback); else showFallback(); }
  if (validRecord) { if (badge) badge.classList.add("visible"); if (recordStatus) recordStatus.textContent = "공유 기록이 이 화면에 첨부되어 있습니다."; }
  else if (recordStatus) recordStatus.textContent = "기록 링크를 받으면 이곳에서 확인할 수 있습니다.";
  if (recordButton) recordButton.addEventListener("click", shareRecord);
}());
