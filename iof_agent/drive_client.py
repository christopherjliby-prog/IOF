"""
Google Drive client for IOF autonomous agent.
Auth: Service Account JSON stored in GOOGLE_SERVICE_ACCOUNT_JSON env var.
The service account must have Editor access to the IOF_System Drive folder.
"""

import json
import os

from google.oauth2 import service_account
from googleapiclient.discovery import build
from googleapiclient.http import MediaInMemoryUpload

SCOPES = ["https://www.googleapis.com/auth/drive"]
FOLDER_ID = "153_lINrPAvKs7GUu4HHlXTDA-tOm2L41"


class DriveClient:
    def __init__(self):
        sa_json = os.environ.get("GOOGLE_SERVICE_ACCOUNT_JSON")
        if not sa_json:
            raise RuntimeError("GOOGLE_SERVICE_ACCOUNT_JSON env var not set")

        sa_info = json.loads(sa_json)
        creds = service_account.Credentials.from_service_account_info(
            sa_info, scopes=SCOPES
        )

        # Optional: impersonate folder owner so files appear under their account
        delegated = os.environ.get("GOOGLE_DELEGATED_EMAIL")
        if delegated:
            creds = creds.with_subject(delegated)

        self.svc = build("drive", "v3", credentials=creds, cache_discovery=False)

    # ── Read ──────────────────────────────────────────────────────────────────

    def list_files(self) -> list[dict]:
        q = f"'{FOLDER_ID}' in parents and trashed = false"
        resp = self.svc.files().list(
            q=q,
            fields="files(id, name, mimeType, modifiedTime)",
            orderBy="modifiedTime desc",
            pageSize=50,
        ).execute()
        return resp.get("files", [])

    def read_file(self, file_id: str, mime_type: str) -> str:
        if "google-apps.document" in mime_type:
            data = self.svc.files().export(
                fileId=file_id, mimeType="text/plain"
            ).execute()
        else:
            data = self.svc.files().get_media(fileId=file_id).execute()

        return data.decode("utf-8", errors="replace") if isinstance(data, bytes) else str(data)

    def read_all(self) -> dict[str, str]:
        """Return {filename: content} for every file in the IOF_System folder."""
        files = self.list_files()
        out = {}
        for f in files:
            try:
                out[f["name"]] = self.read_file(f["id"], f.get("mimeType", ""))
                print(f"  [drive] read  {f['name']}")
            except Exception as exc:
                print(f"  [drive] skip  {f['name']}: {exc}")
        return out

    # ── Write ─────────────────────────────────────────────────────────────────

    def write_file(self, filename: str, content: str) -> str:
        """Create a new file (Drive allows duplicate names; newest wins by convention)."""
        media = MediaInMemoryUpload(
            content.encode("utf-8"),
            mimetype="text/plain",
            resumable=False,
        )
        meta = {
            "name": filename,
            "parents": [FOLDER_ID],
            "mimeType": "text/plain",
        }
        result = self.svc.files().create(
            body=meta, media_body=media, fields="id"
        ).execute()
        print(f"  [drive] wrote {filename} ({result['id']})")
        return result["id"]
