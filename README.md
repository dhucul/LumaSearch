# LumaSearch

LumaSearch is a Windows desktop app for finding files and folders by name and searching for text inside files. It is built with C#, WPF, and .NET 10.

## Features

- Search a directory and its subdirectories for files, folders, or both.
- Match names using **Name contains**, **Exact name**, or **Wildcards**.
- Search for literal text inside files, with an optional **Match case** setting for both names and text.
- Sort results, select multiple items, resize the results area, and open an item's location in Explorer.
- Recycle selected items, restore them to their original locations, or permanently delete them after confirmation.
- Clear displayed output or permanently empty items saved for recovery by LumaSearch.

## Install

Run the [LumaSearch 1.5.7 installer](Installer/LumaSearch-Setup-1.5.7-win-x64.exe) and follow the setup prompts.

The installer includes the .NET runtime and a Windows x64 application. Installing and launching LumaSearch request administrator permission. Setup can also create a desktop shortcut.

## Search

1. Enter a full **Starting directory** path or select **Browse…**.
2. Choose a name-matching mode and enter a file or folder name. Leave the name blank to match all names.
3. Optionally enter **Text inside files**.
4. Choose **Search Files**, **Search Folders**, or both. Enable **Include Hidden Items** if needed.
5. Enable **Match case** to distinguish uppercase and lowercase letters. It is unchecked by default and applies to both the name and text fields.
6. Select **Search**. Use **Cancel** to stop and keep the results found so far.

| Name mode | Example | Behavior |
| --- | --- | --- |
| Name contains | `report` | Finds names containing `report`, such as `Annual Report.pdf`, when Match case is off. |
| Exact name | `report.txt` | Matches the entire name, including its extension. |
| Wildcards | `*.txt` or `report-??.txt` | `*` matches any number of characters; `?` matches one character. |

With **Match case** enabled, `Report` and `report` are different. Wildcard characters are special only in **Wildcards** mode.

### Text search and limits

- Text searches look for literal text within a single line; the text field does not interpret wildcards or regular expressions.
- Files must satisfy both the name and text filters. Folder results use only the name filter.
- Text decoding supports UTF-8 and UTF-16/UTF-32 files marked with a byte-order mark. The app does not extract document text from PDF or Office formats.
- Directory links are never followed. Content searches skip file links.
- Hidden items are excluded by default. Inaccessible or unreadable items are skipped and counted in the status message.
- Searches stop at 20,000 results. Narrow the starting directory or filters if the limit is reached.
- Search settings are locked while an operation is running. Changes apply when you start the next search.

## Work with results

- Click a column heading to sort; click again to reverse the order.
- Use **Shift-click** for a range or **Ctrl-click** for separate items.
- Select one item and choose **Show in Explorer**, or double-click its row, to reveal its location.
- Drag the divider above the results or a column edge to resize the display. The results context menu offers **Expand results area** and **Reset panel and column sizes**.
- Select **Clear output** to clear results, selection, count, and status while keeping search settings and files.

## Delete, restore, and empty saved items

The default delete action is **Send to Recycle Bin**. Review the confirmation before proceeding. You can also choose **Delete permanently (immediate)**; confirmed permanent deletion cannot be undone.

Use **Restore deleted items…** to return items recycled by LumaSearch to their original locations. Their original parent folders must exist, and existing files or folders are never overwritten. Windows' own Restore command returns these items to LumaSearch's protected staging location; use LumaSearch to finish restoring them to their original locations.

**Empty saved items…** permanently removes items recorded for the current Windows user in LumaSearch's recovery storage and the Recycle Bin, including saved folder contents. It asks for confirmation. Restored originals and items recycled by other apps are kept.

An interrupted emptying operation can leave incomplete folders. The recovery list warns about these items and offers **Restore remaining contents**. Pending cleanup remains unresolved until its recovery record and recorded Recycle Bin metadata have been removed; use **Empty saved items…** again after closing any files that are still open.

See [Deletion and recovery](RECOVERY.md) for storage locations, protection requirements, cancellation behavior, and recovery testing. Uninstalling the app does not remove its protected recovery storage.

## Build from source

Use Windows with the **.NET 10 SDK** installed. Run these commands in PowerShell from the repository root:

```powershell
dotnet build LumaSearch.slnx --configuration Release
```

The application is built at `bin\Release\net10.0-windows\LumaSearch.exe`.

### Run tests

```powershell
dotnet Tests\bin\Release\net10.0-windows\LumaSearch.Tests.dll
```

The regression suite uses disposable fixtures to check search behavior, Match case, deletion and recovery, operation state, selection, and WPF layout.

To additionally exercise native Windows recycling, restoration, and saved-item cleanup with disposable fixtures:

```powershell
dotnet Tests\bin\Release\net10.0-windows\LumaSearch.Tests.dll --recycle-smoke
```

The administrator-protection test is documented in [RECOVERY.md](RECOVERY.md).

### Build the installer

Install **Inno Setup 7** at its default location (`%ProgramFiles%\Inno Setup 7`), then run:

```powershell
.\build.ps1
```

The script rebuilds the Release configuration, runs the regression suite, publishes a self-contained Windows x64 application, verifies the release package, and compiles the installer.

| Output | Location |
| --- | --- |
| Self-contained application | `artifacts\publish\Release\win-x64\` |
| Installer | `Installer\LumaSearch-Setup-<version>-win-x64.exe` |

Keep the version in [LumaSearch.csproj](LumaSearch.csproj) and `MyAppVersion` in [Installer/LumaSearch.iss](Installer/LumaSearch.iss) synchronized. Installer compilation rejects a published application whose version does not match.
