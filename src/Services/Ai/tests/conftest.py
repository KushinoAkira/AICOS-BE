"""Tests sign their own tokens, so they need a signing key; it is a throwaway value, not a deployment secret."""
import os

os.environ.setdefault("JWT_SIGNING_KEY", "unit-test-only-signing-key-0123456789abcdef")
