from pathlib import Path


def remove_block(path: str, start_marker: str, end_marker: str) -> None:
    file = Path(path)
    text = file.read_text()
    start = text.find(start_marker)
    if start < 0:
        raise SystemExit(f"missing start marker in {path}: {start_marker!r}")
    end = text.find(end_marker, start)
    if end < 0:
        raise SystemExit(f"missing end marker in {path}: {end_marker!r}")
    file.write_text(text[:start] + text[end:])


remove_block(
    "fe/src/__tests__/rootFolders.reauthorization.store.spec.ts",
    "  it('passes the exact confirmed target path and reloads root folders', async () => {\n",
    "})\n",
)

setup = Path("fe/src/__tests__/test-setup.ts")
text = setup.read_text()
line = "    reauthorizeLegacyRootFolderRelocationTarget: vi.fn(async () => ({})),\n"
if line not in text:
    raise SystemExit("missing legacy relocation API mock")
setup.write_text(text.replace(line, "", 1))
