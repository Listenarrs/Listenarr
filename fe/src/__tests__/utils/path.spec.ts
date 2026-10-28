/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program. If not, see <https://www.gnu.org/licenses/>.
 */
import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'
import { describe, it, expect } from 'vitest'
import {
  toForward,
  trimTrailingSlash,
  trimTrailingDirectorySeparators,
  normalizeForCompare,
  isAbsolutePath,
  isRootedPath,
  hasRelativePathSegment,
  hasParentTraversalSegment,
  hasEmptyMiddlePathSegment,
  hasControlCharacter,
  hasOuterWhitespace,
  hasPathSegmentOuterWhitespace,
  hasWindowsDriveRelativePath,
  hasIncompleteWindowsUncAuthority,
  hasWindowsTrailingSpaceOrPeriodSegment,
  hasWindowsInvalidCharacter,
  pathsOverlap,
  pathsEqual,
  pathIsInside,
  hasWindowsReservedDeviceSegment,
  validateLibraryDestinationPath,
  stripRootPrefix,
  detectPathKind,
  joinPaths,
} from '@/utils/path'

describe('path utils', () => {
  it('toForward converts backslashes to forward', () => {
    expect(toForward('C:\\temp\\dir')).toBe('C:/temp/dir')
    expect(toForward(null)).toBe('')
  })

  it('trimTrailingSlash removes trailing slashes without collapsing drive roots', () => {
    expect(trimTrailingSlash('C:/path/')).toBe('C:/path')
    expect(trimTrailingSlash('C:\\path\\')).toBe('C:\\path')
    expect(trimTrailingSlash('no-slash')).toBe('no-slash')
    expect(trimTrailingSlash('/')).toBe('/')
    expect(trimTrailingSlash('C:\\')).toBe('C:\\')
    expect(trimTrailingSlash('C:\\\\')).toBe('C:\\')
    expect(trimTrailingSlash('C:////')).toBe('C:/')
  })

  it('preserves a trailing backslash as part of a Unix directory name', () => {
    expect(trimTrailingDirectorySeparators('/library/Book\\', 'unix')).toBe('/library/Book\\')
    expect(pathsEqual('/library/Book\\', '/library/Book', 'unix', 'Sensitive')).toBe(false)
    expect(stripRootPrefix('/library', '/library/Book\\', 'Sensitive', 'unix')).toBe('Book\\')
    expect(joinPaths('/library', 'Book\\', 'unix')).toBe('/library/Book\\')
  })

  it('normalizeForCompare lowercases and trims', () => {
    expect(normalizeForCompare('C:\\Temp\\Dir\\')).toBe('c:/temp/dir')
  })

  it('isAbsolutePath respects explicit filesystem context', () => {
    expect(isAbsolutePath('C:\\some\\path')).toBe(true)
    expect(isAbsolutePath('/unix/path')).toBe(true)
    expect(isAbsolutePath('\\library', 'windows')).toBe(false)
    expect(isAbsolutePath('\\', 'windows')).toBe(true)
    expect(isAbsolutePath('\\library', 'unix')).toBe(false)
    expect(isAbsolutePath('C:\\library', 'unix')).toBe(false)
    expect(isAbsolutePath('/library', 'windows')).toBe(false)
    expect(isAbsolutePath('/', 'windows')).toBe(true)
    expect(isAbsolutePath('relative/path')).toBe(false)
  })

  it('distinguishes rooted input from fully absolute paths', () => {
    expect(isRootedPath('C:\\some\\path', 'windows')).toBe(true)
    expect(isRootedPath('\\some\\path', 'windows')).toBe(true)
    expect(isRootedPath('/some/path', 'unix')).toBe(true)
    expect(isRootedPath('\\some\\path', 'unix')).toBe(false)
    expect(isRootedPath('some/path', 'unix')).toBe(false)
    expect(isRootedPath('some\\path', 'windows')).toBe(false)
  })

  it('classifies and rejects Windows drive-relative paths', () => {
    expect(detectPathKind('C:')).toBe('windows')
    expect(detectPathKind('C:relative')).toBe('windows')
    expect(hasWindowsDriveRelativePath('C:')).toBe(true)
    expect(hasWindowsDriveRelativePath('C:relative')).toBe(true)
    expect(hasWindowsDriveRelativePath('C:\\')).toBe(false)
    expect(validateLibraryDestinationPath('C:')).toContain('separator after the drive letter')
    expect(validateLibraryDestinationPath('C:relative')).toContain(
      'separator after the drive letter',
    )
    expect(validateLibraryDestinationPath('C:\\')).toBe(null)
    expect(validateLibraryDestinationPath('C:/')).toBe(null)
    expect(
      validateLibraryDestinationPath('C:\\', {
        pathKind: 'windows',
        allowFileSystemRoot: false,
      }),
    ).toContain('filesystem root')
    expect(
      validateLibraryDestinationPath('/', {
        pathKind: 'unix',
        allowFileSystemRoot: false,
      }),
    ).toContain('filesystem root')
    expect(validateLibraryDestinationPath('Books', { requireAbsolute: true })).toContain(
      'absolute directory path',
    )
    expect(
      validateLibraryDestinationPath('\\library', {
        pathKind: 'unix',
        requireAbsolute: true,
      }),
    ).toContain('absolute directory path')
    expect(validateLibraryDestinationPath('Books')).toBe(null)
    expect(normalizeForCompare('C:\\\\', 'windows')).toBe('c:/')
    expect(stripRootPrefix('C:\\', 'C:\\Books', 'Insensitive', 'windows')).toBe('Books')
    expect(joinPaths('C:\\\\', 'Books', 'windows')).toBe('C:\\Books')
  })

  it('classifies absolute Unix paths with backslashes as Unix paths', () => {
    expect(detectPathKind('/books/Author\\Name')).toBe('unix')
    expect(normalizeForCompare('/books/Author\\Name')).toBe('/books/Author\\Name')
  })

  it('requires context for double-slash absolute paths', () => {
    expect(detectPathKind('//server/share/Books')).toBe('unknown')
    expect(detectPathKind('//server/share/Books', 'windows')).toBe('windows')
    expect(detectPathKind('//server/share/Books', 'unix')).toBe('unix')
    expect(hasEmptyMiddlePathSegment('//server/share/Books')).toBe(false)
    expect(hasEmptyMiddlePathSegment('//server/share/Books', 'windows')).toBe(false)
    expect(hasEmptyMiddlePathSegment('//server/share/Books', 'unix')).toBe(false)
    expect(validateLibraryDestinationPath('//server/share/Books/Author')).toBe(null)
    expect(
      validateLibraryDestinationPath('//server/share/Books/CON', { pathKind: 'windows' }),
    ).toContain('reserved Windows')
    expect(validateLibraryDestinationPath('//server/share/Books/CON', { pathKind: 'unix' })).toBe(
      null,
    )
  })

  it('detects exact relative path segments without blocking periods in names', () => {
    expect(hasRelativePathSegment('D:\\Books\\Title\\.')).toBe(true)
    expect(hasRelativePathSegment('D:\\Books\\Title\\..')).toBe(true)
    expect(hasRelativePathSegment('/books/./title')).toBe(true)
    expect(hasRelativePathSegment('/books/../title')).toBe(true)
    expect(hasRelativePathSegment('/books/Dr. Seuss')).toBe(false)
    expect(hasRelativePathSegment('/books/.metadata')).toBe(false)
    expect(hasRelativePathSegment('/books/title...')).toBe(false)
  })

  it('hasParentTraversalSegment detects parent directory traversal', () => {
    expect(hasParentTraversalSegment('D:\\Books\\Title\\..')).toBe(true)
    expect(hasParentTraversalSegment('/books/title/../other')).toBe(true)
    expect(hasParentTraversalSegment('/books/title..')).toBe(false)
    expect(hasParentTraversalSegment('/books/.../title')).toBe(false)
    expect(hasParentTraversalSegment(null)).toBe(false)
  })

  it('detects empty middle path segments without rejecting roots', () => {
    expect(hasEmptyMiddlePathSegment('D:\\Books\\\\Title')).toBe(true)
    expect(hasEmptyMiddlePathSegment('/books//title')).toBe(true)
    expect(hasEmptyMiddlePathSegment('D:\\Books\\Title')).toBe(false)
    expect(hasEmptyMiddlePathSegment('/books/title')).toBe(false)
    expect(hasEmptyMiddlePathSegment('D:\\')).toBe(false)
    expect(hasEmptyMiddlePathSegment('\\\\server\\share\\Audiobooks')).toBe(false)
    expect(hasEmptyMiddlePathSegment('\\\\server\\share\\\\Audiobooks')).toBe(true)
  })

  it('validates Windows UNC authority structure', () => {
    expect(hasIncompleteWindowsUncAuthority('\\\\server', 'windows')).toBe(true)
    expect(hasIncompleteWindowsUncAuthority('\\\\server\\', 'windows')).toBe(true)
    expect(hasIncompleteWindowsUncAuthority('\\\\server\\share', 'windows')).toBe(false)
    expect(hasIncompleteWindowsUncAuthority('//server/share', 'windows')).toBe(false)
    expect(hasIncompleteWindowsUncAuthority('//server', 'unix')).toBe(false)

    expect(
      validateLibraryDestinationPath('\\\\server', {
        pathKind: 'windows',
        requireAbsolute: true,
      }),
    ).toContain('server and share')
    expect(
      validateLibraryDestinationPath('\\\\server\\share', {
        pathKind: 'windows',
        requireAbsolute: true,
      }),
    ).toBe(null)
    expect(
      validateLibraryDestinationPath('\\\\server\\share', {
        pathKind: 'windows',
        requireAbsolute: true,
        allowFileSystemRoot: false,
      }),
    ).toContain('filesystem root')
    expect(
      validateLibraryDestinationPath('\\\\server\\NUL\\Books', {
        pathKind: 'windows',
      }),
    ).toContain('reserved Windows')
    expect(
      validateLibraryDestinationPath('\\\\NUL\\share\\Books', {
        pathKind: 'windows',
      }),
    ).toContain('reserved Windows')
    expect(validateLibraryDestinationPath('//server', { pathKind: 'unix' })).toBe(null)
  })

  it('detects control characters and segment whitespace', () => {
    expect(hasControlCharacter('D:\\Books\\Title\n')).toBe(true)
    expect(hasControlCharacter('D:\\Books\\Title')).toBe(false)
    expect(hasOuterWhitespace(' D:\\Books\\Title')).toBe(true)
    expect(hasOuterWhitespace('D:\\Books\\Title ')).toBe(true)
    expect(hasOuterWhitespace('D:\\Listenarr Test\\Title')).toBe(false)
    expect(hasPathSegmentOuterWhitespace('D:\\Books\\test ')).toBe(true)
    expect(hasPathSegmentOuterWhitespace('D:\\Books\\ test')).toBe(true)
    expect(hasPathSegmentOuterWhitespace('D:\\Listenarr Test\\Title')).toBe(false)
  })

  it('detects Windows-only trailing space or period segments', () => {
    expect(hasWindowsTrailingSpaceOrPeriodSegment('D:\\Books\\test ')).toBe(true)
    expect(hasWindowsTrailingSpaceOrPeriodSegment('D:\\Books\\test.')).toBe(true)
    expect(hasWindowsTrailingSpaceOrPeriodSegment('D:\\Books\\ test')).toBe(false)
    expect(hasWindowsTrailingSpaceOrPeriodSegment('/books/test ')).toBe(false)
    expect(hasWindowsTrailingSpaceOrPeriodSegment('/books/ test ')).toBe(false)
  })

  it('detects Windows invalid characters and reserved device names', () => {
    expect(hasWindowsInvalidCharacter('D:\\Books\\Bad|Folder')).toBe(true)
    expect(hasWindowsInvalidCharacter('D:\\Books\\Bad:Folder')).toBe(true)
    expect(hasWindowsInvalidCharacter('D:\\Books\\Good Folder')).toBe(false)
    expect(hasWindowsReservedDeviceSegment('D:\\Books\\CON')).toBe(true)
    expect(hasWindowsReservedDeviceSegment('D:\\Books\\NUL.txt')).toBe(true)
    expect(hasWindowsReservedDeviceSegment('D:\\Books\\COM1.folder')).toBe(true)
    expect(hasWindowsReservedDeviceSegment('D:\\Books\\COM¹.folder')).toBe(true)
    expect(hasWindowsReservedDeviceSegment('D:\\Books\\com²')).toBe(true)
    expect(hasWindowsReservedDeviceSegment('D:\\Books\\LPT³.txt')).toBe(true)
    expect(hasWindowsReservedDeviceSegment('\\\\server\\NUL\\Books', 'windows')).toBe(true)
    expect(hasWindowsReservedDeviceSegment('\\\\NUL\\share\\Books', 'windows')).toBe(true)
    expect(hasWindowsReservedDeviceSegment('\\\\server\\COM¹\\Books', 'windows')).toBe(true)
    expect(hasWindowsReservedDeviceSegment('\\\\LPT³\\share\\Books', 'windows')).toBe(true)
    expect(hasWindowsReservedDeviceSegment('D:\\Books\\COM⁴.txt')).toBe(false)
    expect(hasWindowsReservedDeviceSegment('D:\\Books\\Concert')).toBe(false)
  })

  it('matches the shared Windows reserved-device fixture', () => {
    const fixture = JSON.parse(
      readFileSync(
        resolve(process.cwd(), '../test-fixtures/windows-reserved-device-names.json'),
        'utf8',
      ),
    ) as { reserved: string[]; nonReserved: string[] }

    for (const name of fixture.reserved) {
      expect(hasWindowsReservedDeviceSegment(`C:\\Books\\${name}.folder`, 'windows'), name).toBe(
        true,
      )
    }
    for (const name of fixture.nonReserved) {
      expect(hasWindowsReservedDeviceSegment(`C:\\Books\\${name}.folder`, 'windows'), name).toBe(
        false,
      )
    }
  })

  it('detects overlapping source and destination paths', () => {
    expect(pathsOverlap('D:\\Books\\Title\\Child', 'D:\\Books\\Title', 'windows')).toBe(true)
    expect(pathsOverlap('D:\\Books\\Title', 'D:\\Books\\Title\\Child', 'windows')).toBe(true)
    expect(pathsOverlap('D:\\Books\\Title2', 'D:\\Books\\Title', 'windows')).toBe(false)
    expect(pathsOverlap('/books/title/child', '/books/title', 'unix')).toBe(true)
    expect(pathsOverlap('/books/title2', '/books/title', 'unix')).toBe(false)
    expect(pathsOverlap('/Books/title', '/books', 'unix')).toBe(false)
    expect(pathIsInside('/Author/Title', '/', 'unix')).toBe(true)
  })

  it('uses server-provided case sensitivity instead of path shape', () => {
    expect(pathsEqual('/Books/Title', '/books/title', 'unix', 'Insensitive')).toBe(true)
    expect(pathsEqual('C:\\Books\\Title', 'c:\\books\\title', 'windows', 'Sensitive')).toBe(false)
    expect(pathIsInside('/Books/Title', '/books', 'unix', 'Insensitive')).toBe(true)
  })

  it('validates library destination paths while allowing platform-valid whitespace', () => {
    expect(validateLibraryDestinationPath('D:\\Books\\Title\\.')).toContain(
      'current-directory path segments',
    )
    expect(validateLibraryDestinationPath('/books/title/.')).toContain(
      'current-directory path segments',
    )
    expect(validateLibraryDestinationPath('D:\\Books\\Title\\..')).toContain(
      'Path traversal is not allowed',
    )
    expect(validateLibraryDestinationPath('/books/title/..')).toContain(
      'Path traversal is not allowed',
    )
    expect(validateLibraryDestinationPath('D:\\Books\\\\Title')).toContain('empty path segments')
    expect(validateLibraryDestinationPath('D:\\Books\\Bad*Folder')).toContain('invalid on Windows')
    expect(validateLibraryDestinationPath('D:\\Books\\CON.txt')).toContain('reserved Windows')
    expect(validateLibraryDestinationPath('D:\\Books\\test ')).toContain(
      'cannot end with a space or period',
    )
    expect(validateLibraryDestinationPath('D:\\Books\\test.')).toContain(
      'cannot end with a space or period',
    )
    expect(validateLibraryDestinationPath('D:\\Books\\ test')).toBe(null)
    expect(validateLibraryDestinationPath('/books/ test /')).toBe(null)
    expect(validateLibraryDestinationPath('D:\\Books\\Dr. Seuss')).toBe(null)
    expect(validateLibraryDestinationPath('D:\\Books\\.metadata')).toBe(null)
    expect(validateLibraryDestinationPath('D:\\Books\\Title...')).toContain(
      'cannot end with a space or period',
    )
    expect(validateLibraryDestinationPath('/books/Title...')).toBe(null)
    expect(
      validateLibraryDestinationPath('D:\\Books\\Title\\Child', {
        pathKind: 'windows',
        sourcePath: 'D:\\Books\\Title',
      }),
    ).toBe(null)
    expect(
      validateLibraryDestinationPath('/books/title/child', {
        pathKind: 'unix',
        sourcePath: '/books/title',
      }),
    ).toBe(null)
    expect(
      validateLibraryDestinationPath('D:\\Books', {
        pathKind: 'windows',
        sourcePath: 'D:\\Books\\Title',
      }),
    ).toBe(null)
  })

  it('stripRootPrefix removes only a complete root boundary', () => {
    const root = 'C:\\temp\\Isaac Asimov\\Foundation'
    const full = 'C:\\temp\\Isaac Asimov\\Foundation\\Prelude to Foundation'
    expect(stripRootPrefix(root, full)).toBe('Prelude to Foundation')
    expect(stripRootPrefix(root, root)).toBe('')

    const forwardRoot = 'C:/temp/Isaac Asimov/Foundation'
    const forwardFull = 'C:/temp/Isaac Asimov/Foundation/Prelude to Foundation'
    expect(stripRootPrefix(forwardRoot, forwardFull)).toBe('Prelude to Foundation')

    expect(stripRootPrefix('C:/root/other', full)).toBe(null)
    expect(stripRootPrefix('C:/root/books', 'C:/root/bookshelf/Title')).toBe(null)
    expect(stripRootPrefix('C:/root/books/Extra', 'C:/other/root/bookshelf/Title')).toBe(null)
    expect(
      stripRootPrefix(
        'C:/temp/Isaac Asimov/Foundation/Extra',
        'C:/some/prefix/isaac asimov/foundation/Prelude',
      ),
    ).toBe(null)
    expect(stripRootPrefix('C:/Books', 'D:/Books/Title')).toBe(null)
    expect(stripRootPrefix('C:/Books', 'c:/books/Title', 'Sensitive')).toBe(null)
    expect(stripRootPrefix('C:/Books', 'c:/books/Title', 'Insensitive')).toBe('Title')
    expect(stripRootPrefix('\\\\server\\share\\Books', '//server/share/Books/Title')).toBe('Title')
    expect(stripRootPrefix('\\\\server\\share\\Books', '//server/other/Books/Title')).toBe(null)
    expect(
      stripRootPrefix(
        '//server/share/Books',
        '//server/share/Books/Title',
        'Insensitive',
        'windows',
      ),
    ).toBe('Title')
    expect(stripRootPrefix('//srv/library', '//srv/library/Title', 'Sensitive', 'unix')).toBe(
      'Title',
    )
  })
})
