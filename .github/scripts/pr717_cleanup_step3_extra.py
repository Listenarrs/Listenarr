from pathlib import Path

store_test = Path("fe/src/__tests__/rootFolders.reauthorization.store.spec.ts")
text = store_test.read_text()
block = """  it('passes the exact confirmed target path and reloads root folders', async () => {
    const targetPath = '/srv/Audiobooks '
    const result: RootFolderPathChangeResult = {
      relocationId: 'relocation-1',
      rootFolderId: 3,
      currentPath: '/srv/Old',
      targetPath,
      status: 'Running',
      totalJobs: 1,
      completedJobs: 0,
      targetIdentityEnrollmentState: 'Authorized',
    }
    vi.mocked(apiService.reauthorizeLegacyRootFolderRelocationTarget).mockResolvedValueOnce(result)
    vi.mocked(apiService.getRootFolders).mockResolvedValueOnce([])
    const store = useRootFoldersStore()

    await expect(store.reauthorizeLegacyTarget('relocation-1', targetPath)).resolves.toEqual(result)

    expect(apiService.reauthorizeLegacyRootFolderRelocationTarget).toHaveBeenCalledWith(
      'relocation-1',
      targetPath,
    )
    expect(apiService.getRootFolders).toHaveBeenCalledTimes(1)
  })
"""
if block not in text:
    raise SystemExit("missing exact legacy relocation store test")
text = text.replace(block, "", 1)
if text.count("RootFolderPathChangeResult") == 1:
    text = text.replace("import type { RootFolderPathChangeResult } from '@/types'\n", "", 1)
store_test.write_text(text)

setup = Path("fe/src/__tests__/test-setup.ts")
text = setup.read_text()
line = "    reauthorizeLegacyRootFolderRelocationTarget: vi.fn(async () => ({})),\n"
if line not in text:
    raise SystemExit("missing legacy relocation API mock")
setup.write_text(text.replace(line, "", 1))
