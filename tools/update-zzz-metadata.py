#!/usr/bin/env python3
"""Update the ZZZ catalog from the newer of Liyin's GitHub and website catalogs.

Compare numeric catalog versions, preferring GitHub when versions are equal.
Uses the Python standard library only.
"""

import argparse
import hashlib
import http.client
import json
import os
from pathlib import Path
import re
import sys
import tempfile
import time
import urllib.error
import urllib.request


REPOSITORY = "Ticca-Liyin/zzz.liyin.space"
UPSTREAM_PATH = "src/jsons/AchievementInfo.json"
WEBSITE_URL = "https://zzz.liyin.space/jsons/AchievementInfo.json"
MAX_BYTES = 4 * 1024 * 1024
DEFAULT_OUTPUT = (
    Path(__file__).resolve().parents[1]
    / "src/Zeitlind.Protocol/Metadata/ZzzAchievementInfo.json"
)


def version_parts(text):
    if not isinstance(text, str) or not re.fullmatch(r"[0-9]+(?:\.[0-9]+){1,2}", text):
        raise ValueError(f"Invalid catalog version: {text!r}")
    parts = tuple(map(int, text.split(".")))
    return parts + (0,) * (3 - len(parts))


def latest_version(catalog):
    return max((item["Version"] for item in catalog.values()), key=version_parts)


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError(f"Duplicate JSON key: {key}")
        result[key] = value
    return result


def decode_catalog(data):
    if not 0 < len(data) <= MAX_BYTES:
        raise ValueError("Unexpected metadata size")
    catalog = json.loads(data.decode("utf-8-sig"), object_pairs_hook=unique_object)
    if not isinstance(catalog, dict) or len(catalog) < 100:
        raise ValueError("Achievement catalog must contain at least 100 entries")
    for key, item in catalog.items():
        if not re.fullmatch(r"[0-9]{7}", key) or not isinstance(item, dict):
            raise ValueError(f"Invalid achievement entry: {key!r}")
        if type(item.get("Id")) is not int or str(item["Id"]) != key:
            raise ValueError(f"Mismatched achievement ID: {key}")
        for field in ("Name", "Desc"):
            if not isinstance(item.get(field), str) or not item[field].strip():
                raise ValueError(f"Missing {field} for achievement {key}")
        version_parts(item.get("Version"))
    return catalog


def validate_update(current, candidate):
    if len(candidate) * 10 < len(current) * 8:
        raise ValueError(f"Catalog shrank from {len(current)} to {len(candidate)} entries")
    if version_parts(latest_version(candidate)) < version_parts(latest_version(current)):
        raise ValueError("Upstream catalog version is older than the existing catalog")


def fetch_bytes(url, *, token=None):
    headers = {"User-Agent": "Zeitlind-MetadataUpdater/1.0", "Cache-Control": "no-cache"}
    if token:
        headers["Authorization"] = f"Bearer {token}"
    request = urllib.request.Request(url, headers=headers)
    for attempt in range(3):
        try:
            with urllib.request.urlopen(request, timeout=30) as response:
                length = response.headers.get("Content-Length")
                if length is not None and not 0 < int(length) <= MAX_BYTES:
                    raise ValueError("Unexpected HTTP Content-Length")
                data = response.read(MAX_BYTES + 1)
                if not 0 < len(data) <= MAX_BYTES:
                    raise ValueError("Unexpected HTTP body size")
                if length is not None and len(data) != int(length):
                    raise ValueError("HTTP body length does not match Content-Length")
                return data
        except (OSError, http.client.HTTPException) as error:
            if attempt == 2 or (
                isinstance(error, urllib.error.HTTPError)
                and error.code not in (408, 429, 500, 502, 503, 504)
            ):
                raise
            time.sleep(attempt + 1)


def fetch_github():
    token = os.environ.get("GH_TOKEN") or os.environ.get("GITHUB_TOKEN")
    api = f"https://api.github.com/repos/{REPOSITORY}"
    commits = json.loads(fetch_bytes(
        f"{api}/commits?path={UPSTREAM_PATH}&sha=main&per_page=1", token=token
    ))
    if not isinstance(commits, list) or not commits or not isinstance(commits[0], dict):
        raise ValueError("Missing upstream catalog commit")
    commit = commits[0].get("sha")
    if not isinstance(commit, str) or not re.fullmatch(r"[0-9a-f]{40}", commit):
        raise ValueError("Invalid upstream commit")
    # Pin both the metadata lookup and download to the same commit.
    content = json.loads(fetch_bytes(
        f"{api}/contents/{UPSTREAM_PATH}?ref={commit}", token=token
    ))
    if not isinstance(content, dict):
        raise ValueError("Invalid upstream content response")
    blob, size = content.get("sha"), content.get("size")
    if not isinstance(blob, str) or not re.fullmatch(r"[0-9a-f]{40}", blob):
        raise ValueError("Invalid upstream Git blob")
    if type(size) is not int or not 0 < size <= MAX_BYTES:
        raise ValueError("Unexpected upstream metadata size")
    data = fetch_bytes(f"https://raw.githubusercontent.com/{REPOSITORY}/{commit}/{UPSTREAM_PATH}")
    actual_blob = hashlib.sha1(b"blob " + str(len(data)).encode("ascii") + b"\0" + data).hexdigest()
    if len(data) != size or actual_blob != blob:
        raise ValueError("Downloaded catalog does not match the upstream size and Git blob")
    return data, {
        "source_url": f"https://github.com/{REPOSITORY}/blob/{commit}/{UPSTREAM_PATH}",
        "source_commit": commit,
        "source_blob": blob,
    }


def fetch_website():
    return fetch_bytes(WEBSITE_URL), {"source_url": WEBSITE_URL}


def fetch_catalog(current):
    candidates, reports = [], []
    # Always check both sources. Order provides the GitHub tie-breaker.
    for name, fetcher in (("github", fetch_github), ("website", fetch_website)):
        report = {"source": name, "version": "unavailable", "count": "—", "error": ""}
        reports.append(report)
        try:
            data, provenance = fetcher()
            catalog = decode_catalog(data)
            report.update(version=latest_version(catalog), count=len(catalog))
            validate_update(current, catalog)
            candidates.append({
                "name": name, "data": data, "catalog": catalog, "provenance": provenance,
            })
        except (OSError, ValueError, http.client.HTTPException) as error:
            report["error"] = " ".join(str(error).splitlines())
    if not candidates:
        raise ValueError("No usable ZZZ catalog:\n" + "\n".join(
            f"{report['source']}: {report['error']}" for report in reports
        ))
    selected = max(candidates, key=lambda source: version_parts(latest_version(source["catalog"])))
    return selected, reports


def write_catalog(path, data):
    # Keep the downloaded bytes for auditing; replace only after validation.
    temporary = None
    try:
        with tempfile.NamedTemporaryFile(mode="wb", dir=path.parent, delete=False) as stream:
            temporary = Path(stream.name)
            stream.write(data)
        os.replace(temporary, path)
    finally:
        if temporary is not None:
            temporary.unlink(missing_ok=True)


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, default=DEFAULT_OUTPUT, help="Existing catalog to update")
    parser.add_argument("--check", action="store_true", help="Report changes without replacing the catalog")
    parser.add_argument("--github-output", type=Path, help="Append step outputs for GitHub Actions")
    parser.add_argument("--summary", type=Path, help="Append a Markdown update summary")
    args = parser.parse_args(argv)
    with args.output.open("rb") as stream:
        current = decode_catalog(stream.read(MAX_BYTES + 1))
    selected, reports = fetch_catalog(current)
    candidate = selected["catalog"]
    old_ids, new_ids = set(current), set(candidate)
    changed = current != candidate
    comparison = "; ".join(
        f"{r['source']}: {r['version']} ({r['count']} entries)"
        + (" [rejected]" if r["error"] else "") for r in reports
    ) + f"; selected: {selected['name']}"
    result = {
        "changed": str(changed).lower(),
        "old_count": len(current),
        "new_count": len(candidate),
        "old_version": latest_version(current),
        "new_version": latest_version(candidate),
        "added_count": len(new_ids - old_ids),
        "removed_count": len(old_ids - new_ids),
        "modified_count": sum(current[key] != candidate[key] for key in old_ids & new_ids),
        "selected_source": selected["name"],
        "source_comparison": comparison,
        "source_commit": "",
        "source_blob": "",
        **selected["provenance"],
        "source_sha256": hashlib.sha256(selected["data"]).hexdigest(),
    }
    if changed and not args.check:
        write_catalog(args.output, selected["data"])
    if args.github_output:
        with args.github_output.open("a", encoding="utf-8", newline="\n") as stream:
            stream.writelines(f"{key}={value}\n" for key, value in result.items())
    if args.summary:
        with args.summary.open("a", encoding="utf-8", newline="\n") as stream:
            stream.write(
                "## ZZZ Liyin achievement metadata\n\n"
                f"- Version: {result['old_version']} → {result['new_version']}\n"
                f"- Entries: {result['old_count']} → {result['new_count']}\n"
                f"- Added / removed / modified: {result['added_count']} / "
                f"{result['removed_count']} / {result['modified_count']}\n"
                f"- Compared: {comparison}\n"
                f"- Source: {result['source_url']}\n"
                f"- Source SHA-256: `{result['source_sha256']}`\n"
            )
            if result["source_commit"]:
                stream.write(
                    f"- Upstream commit: `{result['source_commit']}`\n"
                    f"- Upstream Git blob: `{result['source_blob']}`\n"
                )
            for report in reports:
                if report["error"]:
                    stream.write(f"- Warning ({report['source']}): {report['error']}\n")
    for report in reports:
        if report["error"]:
            print(f"Warning ({report['source']}): {report['error']}", file=sys.stderr)
    print(json.dumps(result, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    try:
        main()
    except (OSError, ValueError) as error:
        print(f"Metadata update failed: {error}", file=sys.stderr)
        sys.exit(1)
