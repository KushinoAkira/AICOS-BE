"""Optional local CA bundle (.certs/bundle.pem, git-ignored) for machines whose antivirus re-signs HTTPS traffic.
Importing this module points Python's TLS stacks (httpx, requests, huggingface) at it when the file exists."""
import os
from pathlib import Path

_BUNDLE = Path(__file__).resolve().parent.parent / ".certs" / "bundle.pem"
if _BUNDLE.exists():
    for _var in ("SSL_CERT_FILE", "REQUESTS_CA_BUNDLE"):
        os.environ.setdefault(_var, str(_BUNDLE))
