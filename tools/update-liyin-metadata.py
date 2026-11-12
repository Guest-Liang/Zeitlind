#!/usr/bin/env python3
"""Update a ZZZ or HSR catalog from Liyin's GitHub and website catalogs.

Compare numeric catalog versions, preferring GitHub when versions are equal.
Uses the Python standard library only.
"""

import argparse
from dataclasses import dataclass
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


UPSTREAM_PATH = "src/jsons/AchievementInfo.json"
MAX_BYTES = 4 * 1024 * 1024
METADATA_DIRECTORY = Path(__file__).resolve().parents[1] / "src/Zeitlind.Protocol/Metadata"


@dataclass(frozen=True)
class CatalogSource:
    repository: str
    branch: str
    website_url: str
    filename: str
    id_field: str
    minimum_count: int


SOURCES = {
    "zzz": CatalogSource(
        repository="Ticca-Liyin/zzz.liyin.space",
        branch="main",
        website_url="https://zzz.liyin.space/jsons/AchievementInfo.json",
        filename="ZzzAchievementInfo.json",
        id_field="Id",
        minimum_count=100,
    ),
    "hsr": CatalogSource(
        repository="Ticca-Liyin/liyin.space",
        branch="master",
        website_url="https://liyin.space/jsons/AchievementInfo.json",
        filename="HsrAchievementInfo.json",
        id_field="AchievementID",
        minimum_count=1000,
    ),
}


@dataclass(frozen=True)
class Catalog:
    entries: dict
    version: str
    version_key: tuple


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError(f"Duplicate JSON key: {key}")
        result[key] = value
    return result


def decode_catalog(data, game):
    source = SOURCES[game]
    if not 0 < len(data) <= MAX_BYTES:
        raise ValueError("Unexpected metadata size")
    catalog = json.loads(data.decode("utf-8-sig"), object_pairs_hook=unique_object)
    if not isinstance(catalog, dict) or len(catalog) < source.minimum_count:
        raise ValueError(f"Achievement catalog must contain at least {source.minimum_count} entries")
    latest_version, latest_key = "", ()
    for key, item in catalog.items():
        if not re.fullmatch(r"[0-9]{7}", key) or not isinstance(item, dict):
            raise ValueError(f"Invalid achievement entry: {key!r}")
        if game == "hsr" and not key.startswith("4"):
            raise ValueError(f"Invalid HSR achievement ID: {key}")
        embedded_id = item.get(source.id_field)
        if type(embedded_id) is not int or str(embedded_id) != key:
            raise ValueError(f"Mismatched achievement ID: {key}")
        if game == "zzz":
            for field in ("Name", "Desc"):
                if not isinstance(item.get(field), str) or not item[field].strip():
                    raise ValueError(f"Missing {field} for achievement {key}")
        version = item.get("Version")
        if not isinstance(version, str):
            raise ValueError(f"Missing or invalid Version for achievement {key}")
        if game == "hsr":
            version = version.strip()
        if not re.fullmatch(r"[0-9]+(?:\.[0-9]+){1,2}", version):
            raise ValueError(f"Invalid catalog version: {version!r}")
        version_key = tuple(map(int, version.split(".")))
        version_key += (0,) * (3 - len(version_key))
        if version_key > latest_key:
            latest_version, latest_key = version, version_key
    return Catalog(catalog, latest_version, latest_key)


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


def fetch_github(source):
    token = os.environ.get("GH_TOKEN") or os.environ.get("GITHUB_TOKEN")
    repository = source.repository
    api = f"https://api.github.com/repos/{repository}"
    commits = json.loads(fetch_bytes(
        f"{api}/commits?path={UPSTREAM_PATH}&sha={source.branch}&per_page=1", token=token
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
    data = fetch_bytes(f"https://raw.githubusercontent.com/{repository}/{commit}/{UPSTREAM_PATH}")
    actual_blob = hashlib.sha1(b"blob " + str(len(data)).encode("ascii") + b"\0" + data).hexdigest()
    if len(data) != size or actual_blob != blob:
        raise ValueError("Downloaded catalog does not match the upstream size and Git blob")
    return data, {
        "source_url": f"https://github.com/{repository}/blob/{commit}/{UPSTREAM_PATH}",
        "source_commit": commit,
        "source_blob": blob,
    }


def fetch_catalog(current, game):
    source = SOURCES[game]
    old_count = len(current.entries)
    candidates, reports = [], []
    # Always check both sources. Order provides the GitHub tie-breaker.
    for name in ("github", "website"):
        report = {"source": name, "version": "unavailable", "count": "—", "error": ""}
        reports.append(report)
        try:
            if name == "github":
                data, provenance = fetch_github(source)
            else:
                data = fetch_bytes(source.website_url)
                provenance = {"source_url": source.website_url}
            catalog = decode_catalog(data, game)
            new_count = len(catalog.entries)
            report.update(version=catalog.version, count=new_count)
            if new_count * 10 < old_count * 8:
                raise ValueError(f"Catalog shrank from {old_count} to {new_count} entries")
            if catalog.version_key < current.version_key:
                raise ValueError("Upstream catalog version is older than the existing catalog")
            candidates.append({
                "name": name, "data": data, "catalog": catalog, "provenance": provenance,
            })
        except (OSError, ValueError, http.client.HTTPException) as error:
            report["error"] = " ".join(str(error).splitlines())
    if not candidates:
        raise ValueError(f"No usable {game.upper()} catalog:\n" + "\n".join(
            f"{report['source']}: {report['error']}" for report in reports
        ))
    selected = max(candidates, key=lambda candidate: candidate["catalog"].version_key)
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
    parser.add_argument("--game", choices=SOURCES, default="zzz", help="Liyin catalog to check (default: zzz)")
    parser.add_argument("--output", type=Path, help="Existing catalog to update")
    parser.add_argument("--check", action="store_true", help="Report changes without replacing the catalog")
    parser.add_argument("--github-output", type=Path, help="Append step outputs for GitHub Actions")
    parser.add_argument("--summary", type=Path, help="Append a Markdown update summary")
    args = parser.parse_args(argv)
    output = args.output or (METADATA_DIRECTORY / SOURCES[args.game].filename)
    with output.open("rb") as stream:
        current = decode_catalog(stream.read(MAX_BYTES + 1), args.game)
    selected, reports = fetch_catalog(current, args.game)
    candidate = selected["catalog"]
    old_entries, new_entries = current.entries, candidate.entries
    old_ids, new_ids = set(old_entries), set(new_entries)
    changed = old_entries != new_entries
    comparison = "; ".join(
        f"{r['source']}: {r['version']} ({r['count']} entries)"
        + (" [rejected]" if r["error"] else "") for r in reports
    ) + f"; selected: {selected['name']}"
    result = {
        "changed": str(changed).lower(),
        "old_count": len(old_entries),
        "new_count": len(new_entries),
        "old_version": current.version,
        "new_version": candidate.version,
        "added_count": len(new_ids - old_ids),
        "removed_count": len(old_ids - new_ids),
        "modified_count": sum(old_entries[key] != new_entries[key] for key in old_ids & new_ids),
        "selected_source": selected["name"],
        "source_comparison": comparison,
        "source_commit": "",
        "source_blob": "",
        **selected["provenance"],
        "source_sha256": hashlib.sha256(selected["data"]).hexdigest(),
    }
    if changed and not args.check:
        write_catalog(output, selected["data"])
    if args.github_output:
        with args.github_output.open("a", encoding="utf-8", newline="\n") as stream:
            stream.writelines(f"{key}={value}\n" for key, value in result.items())
    if args.summary:
        with args.summary.open("a", encoding="utf-8", newline="\n") as stream:
            stream.write(
                f"## {args.game.upper()} Liyin achievement metadata\n\n"
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
