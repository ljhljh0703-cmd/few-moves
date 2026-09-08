mergeInto(LibraryManager.library, {
  $NectorialOnlineBridge: {
    basePath: "/api/coop/v1",
    storageKey: "fewmoves.online.seats.v1",
    activeKey: "fewmoves.online.current.v1",
    retryKey: "fewmoves.online.retry.v1",
    pollTimer: 0,
    pollInFlight: false,
    generation: 1,
    left: false,
    lastSessionError: "",

    report: function (payload) {
      try {
        SendMessage("CoopOnlineBootstrap", "OnOnlineResult", JSON.stringify(payload));
      } catch (error) {
        // Never log the payload: authenticated responses may contain room state.
      }
    },

    validStoredSeat: function (value) {
      return !!value && typeof value.roomId === "string" && value.roomId.length > 0 &&
        typeof value.seatToken === "string" && value.seatToken.length > 0 && Number.isInteger(value.seat) &&
        (value.seat === 0 || value.seat === 1);
    },

    storedSeats: function () {
      try {
        var raw = localStorage.getItem(NectorialOnlineBridge.storageKey);
        var records = raw ? JSON.parse(raw) : {};
        if (!records || typeof records !== "object") return [];
        var values = [];
        Object.keys(records).forEach(function (key) {
          var value = records[key];
          if (NectorialOnlineBridge.validStoredSeat(value) && key === value.roomId + "|" + value.seat) values.push(value);
        });
        return values;
      } catch (error) {
        NectorialOnlineBridge.lastSessionError = "storage_unavailable";
        return null;
      }
    },

    activateSession: function (value) {
      try {
        sessionStorage.setItem(NectorialOnlineBridge.activeKey, JSON.stringify({ roomId: value.roomId, seat: value.seat }));
        return true;
      } catch (error) {
        NectorialOnlineBridge.lastSessionError = "storage_unavailable";
        return false;
      }
    },

    readSession: function (inviteCode) {
      NectorialOnlineBridge.lastSessionError = "";
      var values = NectorialOnlineBridge.storedSeats();
      if (!values) return null;
      var selected = null;
      var requestedInvite = typeof inviteCode === "string" && inviteCode.length > 0 ? inviteCode : "";
      if (requestedInvite) {
        var matching = values.filter(function (value) { return value.inviteCode === requestedInvite; });
        if (matching.length === 1) selected = matching[0];
        else if (matching.length > 1) {
          NectorialOnlineBridge.lastSessionError = "resume_selection_required";
          return null;
        } else return null;
      } else {
        try {
          var activeRaw = sessionStorage.getItem(NectorialOnlineBridge.activeKey);
          var active = activeRaw ? JSON.parse(activeRaw) : null;
          if (active && typeof active.roomId === "string" && Number.isInteger(active.seat)) {
            selected = values.filter(function (value) { return value.roomId === active.roomId && value.seat === active.seat; })[0] || null;
            if (!selected) sessionStorage.removeItem(NectorialOnlineBridge.activeKey);
          }
        } catch (error) {
          NectorialOnlineBridge.lastSessionError = "storage_unavailable";
          return null;
        }
        if (!selected && values.length === 1) selected = values[0];
        else if (!selected && values.length > 1) {
          NectorialOnlineBridge.lastSessionError = "resume_selection_required";
          return null;
        }
      }
      return selected && NectorialOnlineBridge.activateSession(selected) ? selected : null;
    },

    writeSession: function (value) {
      try {
        var raw = localStorage.getItem(NectorialOnlineBridge.storageKey);
        var records = raw ? JSON.parse(raw) : {};
        records[value.roomId + "|" + value.seat] = value;
        localStorage.setItem(NectorialOnlineBridge.storageKey, JSON.stringify(records));
        sessionStorage.setItem(NectorialOnlineBridge.activeKey, JSON.stringify({ roomId: value.roomId, seat: value.seat }));
        return true;
      } catch (error) { return false; }
    },

    disconnectSession: function () {
      try {
        sessionStorage.removeItem(NectorialOnlineBridge.activeKey);
      } catch (error) {}
    },

    retrySecret: function (scope) {
      try {
        var raw = sessionStorage.getItem(NectorialOnlineBridge.retryKey);
        var values = raw ? JSON.parse(raw) : {};
        if (!values[scope]) {
          values[scope] = NectorialOnlineBridge.randomSecret();
          if (!values[scope]) return null;
          sessionStorage.setItem(NectorialOnlineBridge.retryKey, JSON.stringify(values));
        }
        return values[scope];
      } catch (error) {
        return null;
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
      if (typeof crypto === "undefined" || !crypto.getRandomValues) return null;
      var bytes = new Uint8Array(16);
      crypto.getRandomValues(bytes);
      return Array.prototype.map.call(bytes, function (byte) { return byte.toString(16).padStart(2, "0"); }).join("");
    },

    sanitizedCopy: function (body) {
      if (!body || typeof body !== "object") return body;
      var copy = Object.assign({}, body);
      delete copy.seatToken;
      delete copy.bearer;
      delete copy.createRequestId;
      delete copy.joinRequestId;
      return copy;
    },

    request: function (url, options, operation, onSuccess, generation, onFailure) {
      try {
        fetch(url, options).then(function (response) {
          return response.text().then(function (text) {
            if (generation !== undefined && generation !== NectorialOnlineBridge.generation) return;
            var body = null;
            try { body = text ? JSON.parse(text) : {}; } catch (error) { body = { ok: false, error: { code: "invalid_json_response" } }; }
            if (!response.ok && body.ok !== false) body = { ok: false, error: { code: "http_" + response.status } };
            onSuccess(body, response.status);
          });
        }).catch(function () {
          if (generation !== undefined && generation !== NectorialOnlineBridge.generation) return;
          if (onFailure) onFailure(generation);
          else NectorialOnlineBridge.report({ ok: false, op: operation, error: { code: "network_unavailable" } });
        });
      } catch (error) {
        if (generation !== undefined && generation !== NectorialOnlineBridge.generation) return;
        if (onFailure) onFailure(generation);
        else NectorialOnlineBridge.report({ ok: false, op: operation, error: { code: "network_unavailable" } });
      }
    },

    authHeaders: function (session) {
      return { "Content-Type": "application/json", "Authorization": "Bearer " + session.seatToken };
    },

    startPoll: function () {
      if (NectorialOnlineBridge.pollTimer) return;
      NectorialOnlineBridge.left = false;
      var generation = ++NectorialOnlineBridge.generation;
      NectorialOnlineBridge.pollTimer = setInterval(function () { NectorialOnlineBridge.poll(generation); }, 1000);
      NectorialOnlineBridge.poll(generation);
    },

    stopPoll: function () {
      if (NectorialOnlineBridge.pollTimer) clearInterval(NectorialOnlineBridge.pollTimer);
      NectorialOnlineBridge.pollTimer = 0;
      NectorialOnlineBridge.pollInFlight = false;
      NectorialOnlineBridge.generation += 1;
    },

    completePoll: function (generation) {
      if (generation !== NectorialOnlineBridge.generation) return false;
      NectorialOnlineBridge.pollInFlight = false;
      return true;
    },

    terminalSessionFailure: function (body) {
      var code = body && body.error ? body.error.code : "";
      return code === "room_expired" || code === "seat_token_invalid" || code === "room_access_denied" || code === "room_not_found";
    },

    poll: function (generation) {
      if (NectorialOnlineBridge.left || NectorialOnlineBridge.pollInFlight) return;
      if (generation !== NectorialOnlineBridge.generation) return;
      var session = NectorialOnlineBridge.readSession();
      if (!session) return;
      NectorialOnlineBridge.pollInFlight = true;
      NectorialOnlineBridge.request(NectorialOnlineBridge.basePath + "/rooms/" + encodeURIComponent(session.roomId) + "/state", {
        method: "GET", headers: NectorialOnlineBridge.authHeaders(session), credentials: "same-origin"
      }, "state", function (body) {
        if (!NectorialOnlineBridge.completePoll(generation)) return;
        if (!body.ok && NectorialOnlineBridge.terminalSessionFailure(body)) NectorialOnlineBridge.disconnectSession();
        var safe = NectorialOnlineBridge.sanitizedCopy(body);
        safe.op = "state";
        safe.seat = session.seat;
        NectorialOnlineBridge.report(safe);
      }, generation, function () {
        if (!NectorialOnlineBridge.completePoll(generation)) return;
        NectorialOnlineBridge.report({ ok: false, op: "state", error: { code: "network_unavailable" } });
      });
    },

    resume: function (inviteCode) {
      var session = NectorialOnlineBridge.readSession(inviteCode);
      if (NectorialOnlineBridge.lastSessionError) {
        NectorialOnlineBridge.report({ ok: false, op: "resume", inviteCode: inviteCode || "", seat: -1, error: { code: NectorialOnlineBridge.lastSessionError } });
        return;
      }
      var reportedInvite = session && session.inviteCode ? session.inviteCode : (inviteCode || "");
      NectorialOnlineBridge.report({ ok: true, op: "resume", inviteCode: reportedInvite, seat: session ? session.seat : -1 });
      if (session) NectorialOnlineBridge.startPoll();
    }
  },

  NectorialOnlineResume__deps: ["$NectorialOnlineBridge"],
  NectorialOnlineResume: function () {
    var query = typeof location !== "undefined" ? new URLSearchParams(location.search) : null;
    NectorialOnlineBridge.resume(query ? (query.get("invite") || "") : "");
  },

  NectorialOnlineResumeInvite__deps: ["$NectorialOnlineBridge"],
  NectorialOnlineResumeInvite: function (inviteCodePointer) {
    NectorialOnlineBridge.resume(UTF8ToString(inviteCodePointer) || "");
  },

  NectorialOnlineCreate__deps: ["$NectorialOnlineBridge"],
  NectorialOnlineCreate: function () {
    NectorialOnlineBridge.stopPoll();
    NectorialOnlineBridge.disconnectSession();
    var requestId = NectorialOnlineBridge.retrySecret("create");
    if (!requestId) {
      NectorialOnlineBridge.report({ ok: false, op: "created", error: { code: "secure_random_unavailable" } });
      return;
    }
    var generation = ++NectorialOnlineBridge.generation;
    NectorialOnlineBridge.request(NectorialOnlineBridge.basePath + "/rooms", {
      method: "POST", headers: { "Content-Type": "application/json" }, credentials: "same-origin",
      body: JSON.stringify({ createRequestId: requestId })
    }, "created", function (body) {
      if (body.ok && body.room && body.seatToken) {
        var stored = NectorialOnlineBridge.writeSession({ roomId: body.room.roomId, seat: body.seat, seatToken: body.seatToken, inviteCode: body.inviteCode || "" });
        if (stored) {
          NectorialOnlineBridge.clearRetry("create");
          NectorialOnlineBridge.startPoll();
        } else {
          body = { ok: false, error: { code: "storage_unavailable" } };
        }
      }
      var safe = NectorialOnlineBridge.sanitizedCopy(body);
      safe.op = "created";
      NectorialOnlineBridge.report(safe);
    }, generation);
  },

  NectorialOnlineJoin__deps: ["$NectorialOnlineBridge"],
  NectorialOnlineJoin: function (inviteCodePointer) {
    NectorialOnlineBridge.stopPoll();
    NectorialOnlineBridge.disconnectSession();
    var inviteCode = UTF8ToString(inviteCodePointer);
    if (!inviteCode) {
      NectorialOnlineBridge.report({ ok: false, op: "joined", error: { code: "invite_code_missing" } });
      return;
    }
    var retryScope = "join|" + inviteCode;
    var requestId = NectorialOnlineBridge.retrySecret(retryScope);
    if (!requestId) {
      NectorialOnlineBridge.report({ ok: false, op: "joined", error: { code: "secure_random_unavailable" } });
      return;
    }
    var generation = ++NectorialOnlineBridge.generation;
    NectorialOnlineBridge.request(NectorialOnlineBridge.basePath + "/rooms/join", {
      method: "POST", headers: { "Content-Type": "application/json" }, credentials: "same-origin",
      body: JSON.stringify({ inviteCode: inviteCode, joinRequestId: requestId })
    }, "joined", function (body) {
      var sessionStored = false;
      if (body.ok && body.room && body.seatToken) {
        sessionStored = NectorialOnlineBridge.writeSession({ roomId: body.room.roomId, seat: body.seat, seatToken: body.seatToken, inviteCode: inviteCode });
        if (sessionStored) {
          NectorialOnlineBridge.clearRetry(retryScope);
          NectorialOnlineBridge.startPoll();
        } else {
          body = { ok: false, error: { code: "storage_unavailable" } };
        }
      }
      var safe = NectorialOnlineBridge.sanitizedCopy(body);
      safe.op = "joined";
      if (sessionStored) safe.inviteCode = inviteCode;
      NectorialOnlineBridge.report(safe);
    }, generation);
  },

  NectorialOnlineCommand__deps: ["$NectorialOnlineBridge"],
  NectorialOnlineCommand: function (jsonPointer) {
    var session = NectorialOnlineBridge.readSession();
    if (!session) {
      NectorialOnlineBridge.report({ ok: false, op: "command", error: { code: "seat_session_missing" } });
      return;
    }
    var json = UTF8ToString(jsonPointer);
    var generation = NectorialOnlineBridge.generation;
    NectorialOnlineBridge.request(NectorialOnlineBridge.basePath + "/rooms/" + encodeURIComponent(session.roomId) + "/commands", {
      method: "POST", headers: NectorialOnlineBridge.authHeaders(session), credentials: "same-origin", body: json
    }, "command", function (body) {
      if (!body.ok && NectorialOnlineBridge.terminalSessionFailure(body)) NectorialOnlineBridge.disconnectSession();
      var safe = NectorialOnlineBridge.sanitizedCopy(body);
      safe.op = "command";
      safe.seat = session.seat;
      NectorialOnlineBridge.report(safe);
    }, generation);
  },

  NectorialOnlineLeave__deps: ["$NectorialOnlineBridge"],
  NectorialOnlineLeave: function () {
    NectorialOnlineBridge.left = true;
    NectorialOnlineBridge.stopPoll();
    NectorialOnlineBridge.disconnectSession();
    NectorialOnlineBridge.report({ ok: true, op: "left", seat: -1 });
  },

  NectorialOnlineReportState: function (jsonPointer) {
    var json = UTF8ToString(jsonPointer);
    if (typeof window !== "undefined" && window.__nectorialOnline && window.__nectorialOnline.receiveState) {
      window.__nectorialOnline.receiveState(json);
    }
  }
});
