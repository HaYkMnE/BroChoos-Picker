# Repository metadata and launch texts

Copy these into GitHub repository settings and launch listings. Every product claim here matches the README, which was checked against the code.

## GitHub "About" description

Rule: "BroChoos Picker", "browser picker" and "Windows" all appear in the first 60 characters; the whole text is at most 160 characters.

Lengths below are PowerShell `.Length` counts.

**Final pick** (144 characters; all three terms end by character 50):

```text
BroChoos Picker: a free browser picker for Windows. Click any link, choose the browser that opens it, and the profile for Chrome, Edge or Brave.
```

Alternate 1 (130 characters; all three terms end by character 47):

```text
BroChoos Picker, the browser picker for Windows: open every link in the right browser and Chrome/Edge/Brave profile. No telemetry.
```

Alternate 2 (136 characters; all three terms end by character 44):

```text
BroChoos Picker - browser picker for Windows 10/11. Pick Chrome, Edge or Brave profile per link, or Firefox. Free and open source (MIT).
```

## Website field

```text
https://github.com/HaYkMnE/BroChoos-Picker/releases/latest
```

Replace with a project site if one is created.

## Topics (20)

```text
browser-picker
browser-chooser
browser-selector
default-browser
browser-profiles
chrome-profiles
link-handler
url-handler
open-links
windows
windows-10
windows-11
chrome
edge
brave
firefox
avalonia
dotnet
csharp
productivity
```

All are lowercase with hyphens and under 50 characters.

## Social preview (for the designer)

The final image is versioned at `docs/assets/social-preview.png`. Upload it under **Settings → General → Social preview**.

Final card (1280 × 640 px, PNG), as signed off. The card shows:

- the app icon;
- the name **BroChoos Picker**;
- the tagline "Pick the browser for every link. Even the Chrome, Edge or Brave profile.";
- the badge "Windows 10/11 · Free · Open source";
- the picker window with five tiles.

The longer caption appears only on the hero images (`docs/assets/hero-light.png`, `hero-dark.png`), not on the social card.

## AlternativeTo entry

- **Name:** BroChoos Picker
- **Category:** Browser picker / Default browser chooser
- **Platforms:** Windows
- **License:** Free, open source (MIT)
- **Alternative to:** BrowserPicker, Browser Tamer, Hurl, BrowserSelect, Browser Chooser 2, Choosy, Velja
- **Short description:**

  > BroChoos Picker is a free browser picker for Windows: click any link and choose which browser — and which profile — opens it. Each Chrome, Edge and Brave profile gets its own tile (up to nine tiles in total; extra profiles show as "+N" on that browser's last tile); Firefox gets one tile and opens its default profile. Keyboard-driven (1–9, Enter, Esc), per-user install without admin rights, no telemetry. UI in English, Russian and Armenian.

- **Tags:** browser-picker, default-browser, chrome-profiles, link-handler, privacy

## awesome-windows pull request line

```markdown
* [BroChoos Picker](https://github.com/HaYkMnE/BroChoos-Picker) - Browser and profile picker: choose which browser and Chrome/Edge/Brave profile opens each link. ![Open-Source Software][oss icon]
```

Check the target list's exact format (icon reference, ordering, section) before you open the PR.

## Show HN

Title (within HN's 80-character limit):

```text
Show HN: BroChoos Picker - choose browser and Chrome profile per link on Windows
```

First comment draft:

> I keep separate Chrome profiles for work and personal use, and links from Slack, mail and Teams kept opening in the wrong one. BroChoos Picker registers as your default browser and, when you click a link, shows a small window with one tile per browser, plus one tile per profile for Chrome, Edge and Brave. Press a number and Enter, and the link opens there.
>
> It is .NET + Avalonia, self-contained (no runtime to install), installs per-user without admin rights, and has no telemetry. Network code lives only in a separate helper process; automated tests forbid network APIs in the core and the picker. Firefox opens in its default profile for now. Releases are not code-signed yet, so SmartScreen will warn on the direct download; winget usually avoids that prompt (Windows 11 Smart App Control still applies), and each release has SHA256 sums and a GitHub attestation.
>
> Feedback on what is missing is very welcome.
