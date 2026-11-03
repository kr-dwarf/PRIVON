#!/usr/bin/env python3
"""Rebuilds the U.S. authority snapshots from the verbatim public source files in ./source (no network access).

    python -I -X utf8 build_authority.py

Inputs (verbatim copies of public pages, fetched read-only on RETRIEVED_ON, never edited):
    source/nanpa-npa_report.csv                      https://reports.nanpa.com/public/npa_report.csv
    source/usps-pub28-appendix-c1-street-suffix.htm  https://pe.usps.com/text/pub28/28apc_002.htm
    source/usps-pub28-appendix-b-state-possession.htm https://pe.usps.com/text/pub28/28apb.htm

Outputs (normalized, UTF-8, LF, final LF, one record per line, no padding):
    normalized/us-npa.txt        "<NPA> <JURISDICTION>"   used by us.phone
    normalized/usps-suffix.txt   "<SUFFIX>"               used by us.address
    normalized/usps-state.txt    "<STATE>"                used by us.address
    authority-manifest.json      provenance (URL, dates, counts, SHA-256 of source and of normalized bytes, artifact version)

The normalized bytes are what the product's AuthoritySnapshotLoader hashes, so normalizedSha256 below is the loader's ExpectedSha256Hex.
An independent C# re-derivation of the same three files from ./source lives in the test suite (UsAuthorityProductionTests).
"""
import csv
import hashlib
import html
import io
import json
import re
from pathlib import Path

HERE = Path(__file__).resolve().parent
SRC = HERE / "source"
OUT = HERE / "normalized"

ARTIFACT_VERSION = "us-authority/2026-11-02.1"
RETRIEVED_ON = "2026-11-02"   # the date the files were read; the NANPA file's own header reads "File Date,11/02/2026"
LOADER_SCHEMA_VERSION = "1"   # AuthoritySnapshotLoader accepts exactly this metadata version

# 50 states + DC + the five territories NANPA assigns to the United States (NANPA writes the Northern Mariana Islands as CNMI).
US_JURISDICTIONS = set("""AK AL AR AZ CA CO CT DE FL GA HI IA ID IL IN KS KY LA MA MD ME MI MN MO MS MT NC ND NE NH NJ NM NV NY OH OK OR PA
RI SC SD TN TX UT VA VT WA WI WV WY DC AS CNMI GU PR VI""".split())


def sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def read_table(path: Path, index: int):
    text = path.read_text(encoding="utf-8")
    tables = list(re.finditer(r"<table.*?</table>", text, re.S))
    rows = []
    for row in re.finditer(r"<tr.*?</tr>", tables[index].group(0), re.S):
        cells = re.findall(r"<t[hd][^>]*>(.*?)</t[hd]>", row.group(0), re.S)
        rows.append([re.sub(r"\s+", " ", html.unescape(re.sub(r"<[^>]+>", " ", c))).strip() for c in cells])
    return rows


def npa_records(path: Path):
    """General-purpose (geographic) area codes assigned to the United States or one of its territories and IN SERVICE."""
    rows = list(csv.reader(io.StringIO(path.read_text(encoding="utf-8-sig"), newline="")))
    assert rows[0][0] == "File Date", rows[0]
    header, data = rows[1], [r for r in rows[2:] if r]
    ix = {h: i for i, h in enumerate(header)}
    records = {}
    for r in data:
        if r[ix["COUNTRY"]] == "US" and r[ix["USE"]] == "G" and r[ix["IN_SERVICE"]] == "Y":
            npa, jurisdiction = r[ix["NPA_ID"]], r[ix["LOCATION"]]
            assert re.fullmatch(r"[2-9][0-9]{2}", npa), npa
            assert jurisdiction in US_JURISDICTIONS, (npa, jurisdiction)
            assert npa not in records, npa
            records[npa] = jurisdiction
    return [f"{npa} {records[npa]}" for npa in sorted(records)]


def suffix_records(path: Path):
    """Every suffix form Publication 28 C1 lists: primary names, commonly used forms and the standard abbreviations."""
    rows = read_table(path, 1)
    assert rows[0][0] == "Primary Street Suffix Name", rows[0]
    tokens = set()
    for row in rows[1:]:
        for cell in row:
            assert re.fullmatch(r"[A-Z]{2,}", cell), cell
            tokens.add(cell)
    return sorted(tokens)


def state_records(path: Path):
    """Every code of the Appendix B 'State/Possession' table (the separate 'Military State' table is not part of it)."""
    rows = read_table(path, 1)
    assert rows[0] == ["State/Possession", "Abbreviation"], rows[0]
    codes = set()
    for _, code in rows[1:]:
        assert re.fullmatch(r"[A-Z]{2}", code), code
        codes.add(code)
    return sorted(codes)


def write_lf(path: Path, records):
    data = ("\n".join(records) + "\n").encode("utf-8")
    path.write_bytes(data)
    return data


def main():
    OUT.mkdir(exist_ok=True)
    specs = [
        dict(id="us.npa", usedBy=["us.phone"], source="nanpa-npa_report.csv", out="us-npa.txt", build=npa_records,
             sourceUrl="https://reports.nanpa.com/public/npa_report.csv", catalogUrl="https://nanpa.com/reports/npa-reports",
             selectionRule="COUNTRY=US and USE=G (general purpose / geographic) and IN_SERVICE=Y; the jurisdiction is the file's LOCATION "
                           "(50 states, DC, AS, CNMI, GU, PR, VI). Non-geographic codes, codes not yet in service and every non-US NANP "
                           "country are excluded."),
        dict(id="usps.suffix", usedBy=["us.address"], source="usps-pub28-appendix-c1-street-suffix.htm", out="usps-suffix.txt", build=suffix_records,
             sourceUrl="https://pe.usps.com/text/pub28/28apc_002.htm", catalogUrl=None,
             selectionRule="Union of the three columns of the C1 table (primary street suffix name, commonly used suffix or abbreviation, "
                           "Postal Service standard abbreviation), upper-case, distinct."),
        dict(id="usps.state", usedBy=["us.address"], source="usps-pub28-appendix-b-state-possession.htm", out="usps-state.txt", build=state_records,
             sourceUrl="https://pe.usps.com/text/pub28/28apb.htm", catalogUrl=None,
             selectionRule="Abbreviation column of the Appendix B 'State/Possession' table, as published (59 codes, including the three "
                           "freely associated states USPS lists there). The 'Military State' table is excluded."),
    ]

    snapshots = []
    for spec in specs:
        source = SRC / spec["source"]
        source_bytes = source.read_bytes()
        records = spec["build"](source)
        data = write_lf(OUT / spec["out"], records)
        snapshots.append({
            "id": spec["id"],
            "usedBy": spec["usedBy"],
            "version": LOADER_SCHEMA_VERSION,
            "sourceUrl": spec["sourceUrl"],
            "catalogUrl": spec["catalogUrl"],
            "retrievedOn": RETRIEVED_ON,
            "sourceFile": f"source/{spec['source']}",
            "sourceBytes": len(source_bytes),
            "sourceSha256": sha256(source_bytes),
            "normalizedFile": f"normalized/{spec['out']}",
            "normalizedRecordCount": len(records),
            "normalizedSha256": sha256(data),
            "selectionRule": spec["selectionRule"],
        })

    manifest = {
        "artifactVersion": ARTIFACT_VERSION,
        "generatedBy": "AuthorityData/build_authority.py",
        "note": "Public reference data read once (read-only) on retrievedOn. Never fetched at runtime. Test fixtures (the four-entry D3A "
                "subsets) are NOT part of this artifact.",
        "snapshots": snapshots,
    }
    (HERE / "authority-manifest.json").write_text(json.dumps(manifest, indent=2, ensure_ascii=False) + "\n", encoding="utf-8", newline="\n")
    for s in snapshots:
        print(s["id"], s["normalizedRecordCount"], s["normalizedSha256"])


if __name__ == "__main__":
    main()
