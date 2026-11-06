"""Tests for xisoburn_receiver (standard library only): python -m unittest discover -s pc-receiver -v"""

import hashlib
import http.client
import json
import os
import shutil
import tempfile
import threading
import unittest

import xisoburn_receiver as rx

SECTOR = rx.SECTOR
TOKEN = "testtoken"


class ReceiverTest(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.mkdtemp(prefix="xisoburn-test-")
        self.server = rx.make_server(self.dir, 0, TOKEN, bind="127.0.0.1", console=False)
        self.port = self.server.server_address[1]
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        self.thread.start()

    def tearDown(self):
        self.server.shutdown()
        self.server.receiver.close_all()
        self.server.server_close()
        shutil.rmtree(self.dir, ignore_errors=True)
        # Any exception inside the server (even after a response was sent) is a bug.
        self.assertEqual(self.server.errors, [], "".join(self.server.errors))

    def request(self, method, path, body=None, headers=None, token=TOKEN):
        conn = http.client.HTTPConnection("127.0.0.1", self.port, timeout=10)
        h = {"X-Token": token} if token is not None else {}
        h.update(headers or {})
        conn.request(method, path, body=body, headers=h)
        resp = conn.getresponse()
        data = json.loads(resp.read().decode("utf-8"))
        conn.close()
        return resp.status, data

    def put(self, name, total, lba, data, sha=None):
        first = lba * SECTOR
        headers = {
            "Content-Range": "bytes %d-%d/%d" % (first, first + len(data) - 1, total * SECTOR),
            "X-Content-SHA256": sha if sha is not None else hashlib.sha256(data).hexdigest(),
        }
        return self.request("PUT", "/chunk?name=%s&total_sectors=%d" % (name, total), data, headers)

    @staticmethod
    def sectors(lba, count):
        return b"".join(bytes([(lba + i) % 251]) * SECTOR for i in range(count))

    def test_hello_and_auth(self):
        status, data = self.request("GET", "/hello")
        self.assertEqual(status, 200)
        self.assertEqual(data["app"], "xisoburn-receiver")
        status, _ = self.request("GET", "/hello", token="wrong")
        self.assertEqual(status, 401)
        status, _ = self.request("GET", "/hello", token=None)
        self.assertEqual(status, 401)

    def test_out_of_order_chunks_resume_and_hash(self):
        name, total = "DISC", 100
        status, data = self.request("GET", "/status?name=%s&total_sectors=%d" % (name, total))
        self.assertEqual((status, data["exists"]), (200, False))

        # Out of order, as the multi-pass rescue does: 32..63, then 0..31, then 80..95.
        for lba, count in ((32, 32), (0, 32), (80, 16)):
            status, data = self.put(name, total, lba, self.sectors(lba, count))
            self.assertEqual(status, 200, data)

        status, data = self.request("GET", "/status?name=%s&total_sectors=%d" % (name, total))
        self.assertEqual(status, 200)
        self.assertEqual(data["received"], [[0, 64], [80, 96]])
        self.assertEqual(data["received_sectors"], 80)

        status, data = self.request("POST", "/finish?name=%s&total_sectors=%d&complete=1" % (name, total),
                                    b"# map\n0 64 +\n")
        self.assertEqual(status, 200, data)
        expected = bytearray(total * SECTOR)
        expected[0:64 * SECTOR] = self.sectors(0, 64)
        expected[80 * SECTOR:96 * SECTOR] = self.sectors(80, 16)
        self.assertEqual(data["sha256"], hashlib.sha256(bytes(expected)).hexdigest())
        self.assertEqual(data["missing_sectors"], 20)
        self.assertTrue(os.path.exists(os.path.join(self.dir, "DISC.map")))
        self.assertTrue(os.path.exists(os.path.join(self.dir, "DISC.sha256")))

        # A new server process on the same folder resumes from the received-ranges file.
        self.server.receiver.close_all()
        again = rx.Receiver(self.dir, TOKEN, console=False)
        img = again.image(name, total, create=False)
        self.assertEqual(img.ranges, [[0, 64], [80, 96]])
        img.close()

    def test_rejects_bad_chunks(self):
        name, total = "DISC", 10
        data = self.sectors(0, 2)
        status, _ = self.put(name, total, 0, data, sha="00" * 32)
        self.assertEqual(status, 400)  # hash mismatch
        status, _ = self.request("PUT", "/chunk?name=%s&total_sectors=%d" % (name, total), data,
                                 {"Content-Range": "bytes 1-4096/%d" % (total * SECTOR)})
        self.assertEqual(status, 400)  # not sector-aligned
        status, _ = self.put(name, total, 9, self.sectors(9, 2))
        self.assertEqual(status, 400)  # past the end of the disc
        status, _ = self.put("../evil", total, 0, data)
        self.assertEqual(status, 400)  # path in the name

    def test_conflicting_disc_size(self):
        status, _ = self.put("DISC", 10, 0, self.sectors(0, 1))
        self.assertEqual(status, 200)
        status, data = self.request("GET", "/status?name=DISC&total_sectors=20")
        self.assertEqual(status, 409, data)

    def test_refuses_foreign_existing_file(self):
        with open(os.path.join(self.dir, "OTHER.iso"), "wb") as f:
            f.write(b"x" * SECTOR)
        status, data = self.put("OTHER", 1, 0, self.sectors(0, 1))
        self.assertEqual(status, 409, data)

    def test_range_merging(self):
        img = rx.Image.__new__(rx.Image)
        img.ranges = []
        for s, e in ((10, 20), (30, 40), (20, 30), (0, 5), (5, 10), (50, 60), (45, 55)):
            img.add_range(s, e)
        self.assertEqual(img.ranges, [[0, 40], [45, 60]])


if __name__ == "__main__":
    unittest.main()
