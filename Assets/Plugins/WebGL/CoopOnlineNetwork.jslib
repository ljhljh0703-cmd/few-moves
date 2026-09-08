mergeInto(LibraryManager.library, {
  $NectorialOnlineBridge: {
    basePath: "/api/coop/v1",
    storageKey: "fewmoves.online.seats.v1",
    activeKey: "fewmoves.online.current.v1",
    retryKey: "fewmoves.online.retry.v1",
    pollTimer: 0,
    pollInFlight: false,
    left: false,

    report: function (payload) {
      try {
        SendMessage("CoopOnlineBootstrap", "OnOnlineResult", JSON.stringify(payload));
      } catch (error) {
        // Never log the payload: authenticated responses may contain room state.
      }
    },

    readSession: function () {
      try {
        var activeRaw = sessionStorage.getItem(NectorialOnlineBridge.activeKey);
        if (!activeRaw) return null;
        var active = JSON.parse(activeRaw);
        if (!active || !active.roomId || !Number.isInteger(active.seat)) return null;
        var raw = localStorage.getItem(NectorialOnlineBridge.storageKey);
        var records = raw ? JSON.parse(raw) : {};
        var value = records[active.roomId + "|" + active.seat];
        if (!value || !value.roomId || !value.seatToken || !Number.isInteger(value.seat)) return null;
        return value;
      } catch (error) {
        return null;
      }
    },

    writeSession: function (value) {
      try {
        var raw = localStorage.getItem(NectorialOnlineBridge.storageKey);
        var records = raw ? JSON.parse(raw) : {};
        records[value.roomId + "|" + value.seat] = value;
        localStorage.setItem(NectorialOnlineBridge.storageKey, JSON.stringify(records));
        sessionStorage.setItem(NectorialOnlineBridge.activeKey, JSON.stringify({ roomId: value.roomId, seat: value.seat }));
      } catch (error) {}
    },

    clearSession: function () {
      try {
        var activeRaw = sessionStorage.getItem(NectorialOnlineBridge.activeKey);
        var active = activeRaw ? JSON.parse(activeRaw) : null;
        var raw = localStorage.getItem(NectorialOnlineBridge.storageKey);
        var records = raw ? JSON.parse(raw) : {};
        if (active && active.roomId && Number.isInteger(active.seat)) delete records[active.roomId + "|" + active.seat];
        localStorage.setItem(NectorialOnlineBridge.storageKey, JSON.stringify(records));
        sessionStorage.removeItem(NectorialOnlineBridge.activeKey);
      } catch (error) {}
    },

    retrySecret: function (scope) {
      try {
        var raw = sessionStorage.getItem(NectorialOnlineBridge.retryKey);
        var values = raw ? JSON.parse(raw) : {};
        if (!values[scope]) {
          values[scope] = NectorialOnlineBridge.randomSecret();
          sessionStorage.setItem(NectorialOnlineBridge.retryKey, JSON.stringify(values));
        }
        return values[scope];
      } catch (error) {
        return NectorialOnlineBridge.randomSecret();
      }
    },

    clearRetry: function (scope) {
      try {
        var raw = sessionStorage.getItem(NectorialOnlineBridge.retryKey);
        var values = raw ? JSON.parse(raw) : {};
        delete values[scope];
        sessionStorage.setItem(NectorialOnlineBridge.retryKey, JSON.stringify(values));
      } catch (error) {}
    },

    randomSecret: function () {
      var bytes = new Uint8Array(16);
      if (typeof crypto !== "undefined" && crypto.getRandomValues) crypto.getRandomValues(bytes);
      else for (var index = 0; index < bytes.length; index++) bytes[index] = Math.floor(Math.random() * 256);
      return Array.prototype.map.call(bytes, function (byte) { return byte.toString(16).padStart(2, "0"); }).join("");
    },

    sanitized: function (body) {
      if (!body || typeof body !== "object") return body;
      if (body.seatToken) delete body.seatToken;
      if (body.bearer) delete body.bearer;
      return body;
    },

    request: function (url, options, operation, onSuccess) {
      fetch(url, options).then(function (response) {
        return response.text().then(function (text) {
          var body = null;
          try { body = text ? JSON.parse(text) : {}; } catch (error) { body = { ok: false, error: { code: "invalid_json_response" } }; }
          body = NectorialOnlineBridge.sanitized(body);
          if (!response.ok && body.ok !== false) body = { ok: false, error: { code: "http_" + response.status } };
          onSuccess(body, response.status);
        });
      }).catch(function () {
        NectorialOnlineBridge.report({ ok: false, op: operation, error: { code: "network_unavailable" } });
      });
    },

    authHeaders: function (session) {
      return { "Content-Type": "application/json", "Authorization": "Bearer " + session.seatToken };
    },

    startPoll: function () {
      if (NectorialOnlineBridge.pollTimer) return;
      NectorialOnlineBridge.left = false;
      NectorialOnlineBridge.pollTimer = setInterval(function () { NectorialOnlineBridge.poll(); }, 1000);
      NectorialOnlineBridge.poll();
    },

    stopPoll: function () {
      if (NectorialOnlineBridge.pollTimer) clearInterval(NectorialOnlineBridge.pollTimer);
      NectorialOnlineBridge.pollTimer = 0;
      NectorialOnlineBridge.pollInFlight = false;
    },

    poll: function () {
      if (NectorialOnlineBridge.left || NectorialOnlineBridge.pollInFlight) return;
      var session = NectorialOnlineBridge.readSession();
      if (!session) return;
      NectorialOnlineBridge.pollInFlight = true;
      NectorialOnlineBridge.request(NectorialOnlineBridge.basePath + "/rooms/" + encodeURIComponent(session.roomId) + "/state", {
        method: "GET", headers: NectorialOnlineBridge.authHeaders(session), credentials: "same-origin"
      }, "state", function (body) {
        NectorialOnlineBridge.pollInFlight = false;
        body.op = "state";
        body.seat = session.seat;
        NectorialOnlineBridge.report(body);
      });
    }
  },

  NectorialOnlineResume: function () {
    var session = NectorialOnlineBridge.readSession();
    var query = typeof location !== "undefined" ? new URLSearchParams(location.search) : null;
    NectorialOnlineBridge.report({ ok: true, op: "resume", inviteCode: query ? (query.get("invite") || "") : "", seat: session ? session.seat : -1 });
    if (session) NectorialOnlineBridge.startPoll();
  },

  NectorialOnlineCreate: function () {
    var requestId = NectorialOnlineBridge.retrySecret("create");
    NectorialOnlineBridge.request(NectorialOnlineBridge.basePath + "/rooms", {
      method: "POST", headers: { "Content-Type": "application/json" }, credentials: "same-origin",
      body: JSON.stringify({ createRequestId: requestId })
    }, "created", function (body) {
      if (body.ok && body.room && body.seatToken) {
        NectorialOnlineBridge.clearRetry("create");
        NectorialOnlineBridge.writeSession({ roomId: body.room.roomId, seat: body.seat, seatToken: body.seatToken, inviteCode: body.inviteCode || "" });
        NectorialOnlineBridge.startPoll();
      }
      body.op = "created";
      body.seatToken = undefined;
      NectorialOnlineBridge.report(body);
    });
  },

  NectorialOnlineJoin: function (inviteCodePointer) {
    var inviteCode = UTF8ToString(inviteCodePointer);
    if (!inviteCode) {
      NectorialOnlineBridge.report({ ok: false, op: "joined", error: { code: "invite_code_missing" } });
      return;
    }
    var retryScope = "join|" + inviteCode;
    var requestId = NectorialOnlineBridge.retrySecret(retryScope);
    NectorialOnlineBridge.request(NectorialOnlineBridge.basePath + "/rooms/join", {
      method: "POST", headers: { "Content-Type": "application/json" }, credentials: "same-origin",
      body: JSON.stringify({ inviteCode: inviteCode, joinRequestId: requestId })
    }, "joined", function (body) {
      if (body.ok && body.room && body.seatToken) {
        NectorialOnlineBridge.clearRetry(retryScope);
        NectorialOnlineBridge.writeSession({ roomId: body.room.roomId, seat: body.seat, seatToken: body.seatToken, inviteCode: inviteCode });
        NectorialOnlineBridge.startPoll();
      }
      body.op = "joined";
      body.seatToken = undefined;
      NectorialOnlineBridge.report(body);
    });
  },

  NectorialOnlineCommand: function (jsonPointer) {
    var session = NectorialOnlineBridge.readSession();
    if (!session) {
      NectorialOnlineBridge.report({ ok: false, op: "command", error: { code: "seat_session_missing" } });
      return;
    }
    var json = UTF8ToString(jsonPointer);
    NectorialOnlineBridge.request(NectorialOnlineBridge.basePath + "/rooms/" + encodeURIComponent(session.roomId) + "/commands", {
      method: "POST", headers: NectorialOnlineBridge.authHeaders(session), credentials: "same-origin", body: json
    }, "command", function (body) {
      body.op = "command";
      body.seat = session.seat;
      NectorialOnlineBridge.report(body);
    });
  },

  NectorialOnlineLeave: function () {
    NectorialOnlineBridge.left = true;
    NectorialOnlineBridge.stopPoll();
    NectorialOnlineBridge.clearSession();
    NectorialOnlineBridge.report({ ok: true, op: "left", seat: -1 });
  },

  NectorialOnlineReportState: function (jsonPointer) {
    var json = UTF8ToString(jsonPointer);
    if (typeof window !== "undefined" && window.__nectorialOnline && window.__nectorialOnline.receiveState) {
      window.__nectorialOnline.receiveState(json);
    }
  }
});
