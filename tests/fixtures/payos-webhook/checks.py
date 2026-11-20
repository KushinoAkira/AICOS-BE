"""Exercises the real Nginx config against upstream.py on a scratch-only network."""
import hashlib
import http.client
import json
import sys
import unittest

PATH = "/api/v1/payments/payos/webhook"
BODY = '{"data":{"description":"Cà phê đá ☕","amount":1234},"signature":"SYNTHETIC"}\n'.encode()


def request(method, path, body=None, headers=None, chunked=False, host="proxy"):
    connection = http.client.HTTPConnection(host, 8080, timeout=10)
    try:
        connection.request(method, path, body=body, headers=headers or {}, encode_chunked=chunked)
        response = connection.getresponse()
        return response.status, response.read()
    finally:
        connection.close()


def count():
    status, body = request("GET", "/__count", host="127.0.0.1")
    assert status == 200
    return json.loads(body)["calls"]


class ProxyChecks(unittest.TestCase):
    def test_body_and_untrusted_headers(self):
        sensitive = {"Authorization": "Bearer SYNTHETIC-NOT-JWT", "Cookie": "synthetic=1",
                     "X-Api-Key": "SYNTHETIC-NOT-KEY", "X-Client-Id": "SYNTHETIC",
                     "X-Forwarded-Host": "invalid.example", "Forwarded": "host=invalid.example",
                     "X-Original-URL": "/api/v1/identity/users", "X-HTTP-Method-Override": "DELETE"}
        status, raw = request("POST", PATH, BODY, sensitive)
        self.assertEqual(status, 200)
        value = json.loads(raw)
        self.assertEqual(value["sha256"], hashlib.sha256(BODY).hexdigest())
        self.assertEqual(value["length"], len(BODY))
        self.assertEqual(value["path"], PATH)
        headers = {key.lower(): item for key, item in value["headers"].items()}
        for key in sensitive:
            self.assertNotIn(key.lower(), headers)
        self.assertEqual(headers["host"], "gateway")
        self.assertEqual(headers["content-type"], "application/json")

    def test_chunked_body_preserved(self):
        status, raw = request("POST", PATH, [BODY[:19], BODY[19:]], chunked=True)
        self.assertEqual(status, 200)
        self.assertEqual(json.loads(raw)["sha256"], hashlib.sha256(BODY).hexdigest())

    def test_methods_do_not_reach_upstream(self):
        before = count()
        for method in ("GET", "HEAD", "OPTIONS", "PUT", "PATCH", "DELETE", "TRACE"):
            with self.subTest(method=method):
                self.assertEqual(request(method, PATH)[0], 405)
        self.assertEqual(count(), before)

    def test_unrelated_paths_do_not_reach_upstream(self):
        before = count()
        for path in ("/", "/health/live", "/swagger/index.html", "/api/v1/identity/auth/login",
                     "/api/v1/identity/users", "/api/v1/payments/bills", "/__count"):
            with self.subTest(path=path):
                self.assertEqual(request("POST", path, BODY)[0], 404)
        self.assertEqual(count(), before)

    def test_path_normalization_cannot_bypass_allowlist(self):
        before = count()
        for path in (PATH + "?orderCode=123", PATH + "/", PATH + "/extra", PATH.replace("webhook", "%77ebhook"),
                     PATH.replace("/payments/", "//payments/"), "/extra/.." + PATH):
            with self.subTest(path=path):
                self.assertEqual(request("POST", path, BODY)[0], 404)
        self.assertEqual(count(), before)

    def test_body_limit(self):
        before = count()
        self.assertEqual(request("POST", PATH, b"x" * (32 * 1024 + 1))[0], 413)
        self.assertEqual(count(), before)

    def test_browser_return_is_forced_readonly_and_drops_body_and_credentials(self):
        path = "/api/v1/payments/payos/return?orderCode=123&status=PAID"
        status, raw = request("GET", path, b"SYNTHETIC", {"X-Aicos-Return-Read-Only": "0", "Authorization": "SYNTHETIC", "Cookie": "synthetic=1"})
        self.assertEqual(status, 200)
        value = json.loads(raw)
        self.assertEqual(value["path"], path)
        self.assertEqual(value["length"], 0)
        headers = {key.lower(): item for key, item in value["headers"].items()}
        self.assertEqual(headers["x-aicos-return-read-only"], "1")
        self.assertNotIn("authorization", headers)
        self.assertNotIn("cookie", headers)

    def test_result_page_ignores_untrusted_payment_status(self):
        before = count()
        normal = request("GET", "/payos/result")
        forged = request("GET", "/payos/result?status=PAID&message=FORGED_BANK_RECEIPT")
        self.assertEqual(normal[0], 200)
        self.assertEqual(forged, normal)
        self.assertIn("Vui lòng quay lại", normal[1].decode())
        self.assertNotIn(b"FORGED_BANK_RECEIPT", normal[1])
        connection = http.client.HTTPConnection("proxy", 8080, timeout=10)
        try:
            connection.request("GET", "/payos/result")
            response = connection.getresponse()
            response.read()
            self.assertEqual(response.getheader("Cache-Control"), "no-store")
            self.assertEqual(response.getheader("Referrer-Policy"), "no-referrer")
            self.assertEqual(response.getheader("X-Content-Type-Options"), "nosniff")
            self.assertIn("default-src 'none'", response.getheader("Content-Security-Policy"))
        finally:
            connection.close()
        self.assertEqual(count(), before)

    def test_browser_routes_reject_other_methods_and_normalized_paths(self):
        before = count()
        for path in ("/api/v1/payments/payos/return", "/payos/result"):
            for method in ("POST", "HEAD", "OPTIONS", "PUT", "PATCH", "DELETE"):
                with self.subTest(path=path, method=method):
                    self.assertEqual(request(method, path)[0], 405)
            for variant in (path + "/", path + "/extra", "/extra/.." + path, path.replace("/payos/", "/%70ayos/")):
                with self.subTest(path=variant):
                    self.assertEqual(request("GET", variant)[0], 404)
        self.assertEqual(count(), before)


if __name__ == "__main__":
    if "--ready" in sys.argv:
        try:
            sys.exit(0 if request("GET", PATH)[0] == 405 else 1)
        except OSError:
            sys.exit(1)
    unittest.main(testRunner=unittest.TextTestRunner(stream=sys.stdout, verbosity=2))
