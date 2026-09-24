#!/usr/bin/env python3
"""Convert Yae's schicksal/metadata into Zeitlind's Genshin achievement catalog.

Schema: https://github.com/HolographicHat/Yae/blob/master/YaeAchievement/res/proto/AchievementInfo.proto
Endpoints: https://github.com/HolographicHat/Yae/blob/master/YaeAchievement/src/Utils.cs
Only achievement descriptions are imported; protocol fields and native offsets are ignored.
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
import urllib.request


SOURCE_URLS = (
    "https://rin.holohat.work/schicksal/metadata",
    "https://ena-rin.holohat.work//schicksal/metadata",
    "https://cn-cd-1259389942.file.myqcloud.com/schicksal/metadata",
)
MAX_BYTES = 4 * 1024 * 1024
DEFAULT_OUTPUT = (
    Path(__file__).resolve().parents[1]
    / "src/Zeitlind.Protocol/Metadata/GenshinAchievementInfo.json"
)


def read_varint(data, offset):
    value = 0
    for shift in range(0, 70, 7):
        if offset >= len(data):
            raise ValueError("Truncated Protobuf varint")
        byte = data[offset]
        offset += 1
        if shift == 63 and byte > 1:
            raise ValueError("Protobuf varint exceeds uint64")
        value |= (byte & 127) << shift
        if byte < 128:
            return value, offset
    raise ValueError("Invalid Protobuf varint")


def fields(data):
    result = []
    offset = 0
    while offset < len(data):
        if len(result) >= 65_536:
            raise ValueError("Too many Protobuf fields")
        tag, offset = read_varint(data, offset)
        number, wire = tag >> 3, tag & 7
        if not 0 < number <= 0x1FFFFFFF:
            raise ValueError("Invalid Protobuf field number")
        if wire == 0:
            value, offset = read_varint(data, offset)
        elif wire in (1, 2, 5):
            if wire == 2:
                size, offset = read_varint(data, offset)
            else:
                size = 8 if wire == 1 else 4
            if size > len(data) - offset:
                raise ValueError("Truncated Protobuf field")
            value = data[offset : offset + size]
            offset += size
        else:
            raise ValueError(f"Unsupported Protobuf wire type: {wire}")
        result.append((number, wire, value))
    return result


def single(message, number, wire, default=None):
    matches = [(kind, value) for field, kind, value in message if field == number]
    if not matches and default is not None:
        return default
    if len(matches) != 1 or matches[0][0] != wire:
        raise ValueError(f"Missing, duplicate, or mistyped Protobuf field {number}")
    return matches[0][1]


def version_parts(text):
    if not isinstance(text, str) or not re.fullmatch(r"[0-9]+(?:\.[0-9]+){1,2}", text):
        raise ValueError(f"Invalid catalog version: {text!r}")
    parts = tuple(map(int, text.split(".")))
    return parts + (0,) * (3 - len(parts))


def normalize_version(text):
    major, minor, patch = version_parts(text)
    return f"{major}.{minor}" + (f".{patch}" if patch else "")


def validate_catalog(catalog):
    if not isinstance(catalog, dict) or len(catalog) < 1_000:
        raise ValueError("Achievement catalog must contain at least 1,000 entries")
    for key, item in catalog.items():
        if not re.fullmatch(r"8[0-9]{4}", key) or not isinstance(item, dict):
            raise ValueError(f"Invalid achievement entry: {key!r}")
        if type(item.get("Id")) is not int or item["Id"] != int(key):
            raise ValueError(f"Mismatched achievement ID: {key}")
        group = item.get("Group")
        if "Group" not in item or (
            group is not None and (type(group) is not int or not 0 <= group <= 0xFFFFFFFF)
        ):
            raise ValueError(f"Invalid achievement group: {key}")
        for field in ("Name", "Desc"):
            if not isinstance(item.get(field), str) or not item[field].strip():
                raise ValueError(f"Missing {field} for achievement {key}")
        version_parts(item.get("Version"))


def decode_catalog(data):
    if not 0 < len(data) <= MAX_BYTES:
        raise ValueError("Unexpected metadata size")
    root = fields(data)
    version = normalize_version(single(root, 1, 2).decode("utf-8"))
    catalog = {}
    for number, wire, value in root:
        if number != 3:  # AchievementInfo.items; ignore groups and executable config.
            continue
        if wire != 2:
            raise ValueError("Achievement map must be length-delimited")
        entry = fields(value)
        achievement_id = single(entry, 1, 0)
        item = fields(single(entry, 2, 2))
        key = str(achievement_id)
        if key in catalog:
            raise ValueError(f"Duplicate achievement ID: {key}")
        catalog[key] = {
            "Id": achievement_id,
            "Group": single(item, 2, 0, default=0) or None,
            "Name": single(item, 3, 2).decode("utf-8"),
            "Desc": single(item, 4, 2).decode("utf-8"),
            # Yae gives the catalog version, not each achievement's release version.
            "Version": version,
        }
    validate_catalog(catalog)
    return dict(sorted(catalog.items(), key=lambda pair: int(pair[0])))


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError(f"Duplicate JSON key: {key}")
        result[key] = value
    return result


def load_catalog(path):
    with path.open("rb") as stream:
        data = stream.read(MAX_BYTES + 1)
    if len(data) > MAX_BYTES:
        raise ValueError("Existing catalog exceeds size limit")
    catalog = json.loads(data.decode("utf-8-sig"), object_pairs_hook=unique_object)
    validate_catalog(catalog)
    return catalog


def latest_version(catalog):
    return max((item["Version"] for item in catalog.values()), key=version_parts)


def validate_update(current, candidate):
    if len(candidate) * 10 < len(current) * 8:
        raise ValueError(f"Catalog shrank from {len(current)} to {len(candidate)} entries")
    if version_parts(latest_version(candidate)) < version_parts(latest_version(current)):
        raise ValueError("Upstream catalog version is older than the existing catalog")


def fetch_catalog(current):
    errors = []
    for url in SOURCE_URLS:
        try:
            request = urllib.request.Request(url, headers={"User-Agent": "Zeitlind-MetadataUpdater/1.0"})
            with urllib.request.urlopen(request, timeout=30) as response:
                length = response.headers.get("Content-Length")
                if length is not None and not 0 < int(length) <= MAX_BYTES:
                    raise ValueError("Unexpected HTTP Content-Length")
                data = response.read(MAX_BYTES + 1)
                if length is not None and len(data) != int(length):
                    raise ValueError("HTTP body length does not match Content-Length")
            candidate = decode_catalog(data)
            validate_update(current, candidate)
            return candidate, url, hashlib.sha256(data).hexdigest()
        except (OSError, ValueError, http.client.HTTPException) as error:
            errors.append(f"{url}: {error}")
    raise ValueError("All Yae metadata endpoints failed:\n" + "\n".join(errors))


def write_catalog(path, catalog):
    # Replace only after download, conversion, and validation have all succeeded.
    temporary = None
    try:
        with tempfile.NamedTemporaryFile(
            mode="w", encoding="utf-8", newline="\n", dir=path.parent, delete=False
        ) as stream:
            temporary = Path(stream.name)
            json.dump(catalog, stream, ensure_ascii=False, indent=2)
            stream.write("\n")
        os.replace(temporary, path)
    finally:
        if temporary is not None:
            temporary.unlink(missing_ok=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, default=DEFAULT_OUTPUT, help="Existing catalog to update")
    parser.add_argument("--source-file", type=Path, help="Read a downloaded Yae Protobuf file instead of HTTP")
    parser.add_argument("--check", action="store_true", help="Report changes without replacing the catalog")
    parser.add_argument("--github-output", type=Path, help="Append step outputs for GitHub Actions")
    parser.add_argument("--summary", type=Path, help="Append a Markdown update summary")
    args = parser.parse_args()
    current = load_catalog(args.output)
    if args.source_file:
        with args.source_file.open("rb") as stream:
            data = stream.read(MAX_BYTES + 1)
        candidate = decode_catalog(data)
        validate_update(current, candidate)
        source_url, source_sha256 = "local-file", hashlib.sha256(data).hexdigest()
    else:
        candidate, source_url, source_sha256 = fetch_catalog(current)

    old_ids, new_ids = set(current), set(candidate)
    changed = current != candidate
    result = {
        "changed": str(changed).lower(),
        "old_count": len(current),
        "new_count": len(candidate),
        "old_version": latest_version(current),
        "new_version": latest_version(candidate),
        "added_count": len(new_ids - old_ids),
        "removed_count": len(old_ids - new_ids),
        "modified_count": sum(current[key] != candidate[key] for key in old_ids & new_ids),
        "source_url": source_url,
        "source_sha256": source_sha256,
    }
    if changed and not args.check:
        write_catalog(args.output, candidate)
    if args.github_output:
        with args.github_output.open("a", encoding="utf-8", newline="\n") as stream:
            stream.writelines(f"{key}={value}\n" for key, value in result.items())
    if args.summary:
        with args.summary.open("a", encoding="utf-8", newline="\n") as stream:
            stream.write(
                "## Genshin Yae achievement metadata\n\n"
                f"- Version: {result['old_version']} → {result['new_version']}\n"
                f"- Entries: {result['old_count']} → {result['new_count']}\n"
                f"- Added / removed / modified: {result['added_count']} / "
                f"{result['removed_count']} / {result['modified_count']}\n"
                f"- Source: {source_url}\n"
                f"- Source SHA-256: `{source_sha256}`\n\n"
                "Version denotes the catalog snapshot, not each achievement's release version.\n"
            )
    print(json.dumps(result, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    try:
        main()
    except (OSError, ValueError) as error:
        print(f"Metadata update failed: {error}", file=sys.stderr)
        sys.exit(1)
