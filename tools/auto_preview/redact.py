"""Redaction applied to every log line and receipt field the preview pipeline writes."""
from __future__ import annotations

import re

_URL = re.compile(r"https?://[^\s\"'<>]+")
_SIGNED = re.compile(r"(?i)[?&](x-amz-|signature|sig=|token|expires|credential|key=|policy)")
_SECRET = re.compile(r"(?i)(bearer\s+[A-Za-z0-9._~+/=-]{8,}|\"(?:token|accessToken|refreshToken|cookie|authorization|apiKey|api_key)\"\s*:\s*\"[^\"]*\")")


class Redactor:
    def __init__(self, secrets: list[str] | None = None):
        self.secrets = sorted({s for s in (secrets or []) if s and len(s) >= 4}, key=len, reverse=True)

    def __call__(self, text: str) -> str:
        for secret in self.secrets:
            text = text.replace(secret, "<redacted-key>")
        text = _URL.sub(lambda m: "<redacted-signed-url>" if "?" in m.group(0) and _SIGNED.search(m.group(0)) else m.group(0), text)
        return _SECRET.sub("<redacted-secret>", text)
