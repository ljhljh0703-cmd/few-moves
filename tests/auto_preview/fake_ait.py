#!/usr/bin/env python3
"""Test double for `ait deploy --scheme-only` (tests only; the receipt marks fake_cli_for_tests).

Checks the call contract the pipeline must honour (HOME unchanged, profile in the injected credentials file, 0600,
no key in argv or env), then behaves per FAKE_AIT_BEHAVIOR: success | fail | leak.
"""
import json
import os
import sys
from pathlib import Path

args = sys.argv[1:]
assert os.environ.get("HOME") == os.environ["FAKE_EXPECT_HOME"], "HOME must not be changed"
cred = Path(os.environ["FAKE_AIT_CREDENTIALS"])
data = json.loads(cred.read_text())
assert list(data) == ["few-moves-preview"], "only our profile"
key = data["few-moves-preview"]
assert oct(cred.stat().st_mode & 0o777) == "0o600", "credentials file must be 0600"
assert key not in " ".join(args), "key must not be in argv"
assert "FEW_MOVES_AIT_DEPLOY_KEY" not in os.environ and key not in "".join(os.environ.values()), "key must not reach the CLI env"
assert args[0] == "deploy" and "--scheme-only" in args and args[args.index("--profile") + 1] == "few-moves-preview"
log = os.environ.get("FAKE_AIT_CALLS")
if log:
    with open(log, "a") as handle:
        handle.write(json.dumps(args) + "\n")
behavior = os.environ.get("FAKE_AIT_BEHAVIOR", "success")
deployment = os.environ["FAKE_AIT_DEPLOYMENT_ID"]
if behavior == "fail":
    print("업로드에 실패했습니다. 다시 시도해주세요.")
    sys.exit(1)
if behavior == "leak":
    print(f"debug key={key} url=https://s3.example.com/x?X-Amz-Signature=abc&X-Amz-Credential=zzz Bearer {key}")
print(f"intoss-private://few-moves?_deploymentId={deployment}&host=appsInTossHost")
