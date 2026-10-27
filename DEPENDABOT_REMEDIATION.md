# Dependabot remediation tracker - 2026-10-26

## Remediation progress - 2026-10-27

The remediation branch updates all three npm dependency trees and upgrades ImageSharp to 4.1.3. It also pins the vulnerable test dependency `Microsoft.AspNetCore.SignalR.Common` to 10.0.13, with the required Options and DependencyInjection.Abstractions patch versions. The approved assembly-scoped Community license and renewal details are in `licenses/`.

The refreshed inventory contains 116 open alerts. The additional alert is [Dependabot 252](https://github.com/Listenarrs/Listenarr/security/dependabot/252), [GHSA-rj75-hqrm-r3gf](https://github.com/advisories/GHSA-rj75-hqrm-r3gf), for `postcss-selector-parser` in `fe/package-lock.json`; the candidate resolves it to 7.1.6.

Validation completed so far: clean installs and complete npm audits for all three trees (zero vulnerabilities), installed dependency-tree checks, all 116 advisory ranges checked against every matching locked instance, 681 frontend tests, frontend lint/type/build checks, a licensed solution build, 31 focused image/security integration tests, and the production Docker build. Controlled runtime probes cover shell quoting, HTTP redirects/readiness, child-process orchestration, HTML sanitization, Discord REST, and malformed content-encoding recovery.

The final production container serves PNG/JPEG/WebP cache fixtures byte-for-byte, handles the malformed BigTIFF fixture in 19 ms, remains ready, and starts the actual bundled bot with a real settings-hub connection. The bot check first asserts that Discord is disabled and no credentials are configured; real Discord gateway authentication and slash-command registration are not exercised.

The full native backend suite and formatting check are being completed separately. Keep this issue open until final CI and default-branch alert reconciliation are complete. The checkboxes below preserve the original baseline and do not assert GitHub has closed the alerts before merge.

## Original baseline inventory

Track all 115 open Dependabot alerts returned by the paginated GitHub API on October 26, 2026 for Listenarrs/Listenarr. The snapshot covers 17 package names across three npm lockfiles: 2 critical, 44 high, 55 moderate (API: medium), and 14 low alerts.

Source: [Dependabot alerts](https://github.com/Listenarrs/Listenarr/security/dependabot). Baseline: `canary` at `e81d83cbe2ac3a0cf23e1fe46de4412f67239ef2`. Alert state can change after this snapshot; refresh the complete paginated inventory before closing the tracker.

Tracking issue: [#1104](https://github.com/Listenarrs/Listenarr/issues/1104). Use the issue checkboxes as the live progress record; the entries in this document preserve the initial baseline.

Severity and dependency scope below are reported by GitHub; they do not establish exploitability in Listenarr or whether a package ships in the deployed application. The progress section above records implementation and validation separately from alert state.

## Remediation plan

- [ ] Address the two critical shell-quote alerts (Dependabot 224 and 232), then high-severity dependencies, followed by moderate and low alerts. Group related upgrades by dependency tree and lockfile.
- [ ] Update the root development tooling dependency tree and regenerate `package-lock.json` (31 alerts). Inspect parent constraints for transitive packages rather than blindly overriding incompatible major versions.
- [ ] Update the frontend runtime and development dependency trees and regenerate `fe/package-lock.json` (61 alerts). Keep Vue compiler/runtime packages and Vitest-related packages compatible.
- [ ] Update the Discord bot dependency tree and regenerate `tools/discord-bot/package-lock.json` (23 alerts). Revisit the existing undici override and sanitize-html constraint.
- [ ] Reuse or supersede overlapping dependency PRs after checking their final resolved versions against every affected range below. A PR title alone does not prove all advisories are fixed.
- [ ] Validate each changed tree with a clean `npm ci`, `npm ls`, and full `npm audit --json`; record residual findings. Run frontend type checks, lint, unit tests and production build, root tooling smoke checks, and controlled Discord bot startup/command/SignalR checks without posting to a live server.
- [ ] Confirm the final dependency changes pass required CI and that GitHub marks each alert fixed after merge into the default branch. Record evidence and rationale for any explicitly accepted exception; do not dismiss alerts merely to clear the dashboard.

First-patched versions below are per-advisory lower bounds, not a blanket upgrade recommendation. Select versions outside all applicable affected ranges and preserve the repository Node.js compatibility requirements. Multiple alerts may refer to different installed major-version ranges of one package.

## Existing dependency PRs to reconcile

- [#682](https://github.com/Listenarrs/Listenarr/pull/682) - Bump vue from 3.5.35 to 3.5.38 in /fe
- [#694](https://github.com/Listenarrs/Listenarr/pull/694) - Bump form-data from 4.0.5 to 4.0.6 in /fe
- [#773](https://github.com/Listenarrs/Listenarr/pull/773) - Bump axios from 1.17.0 to 1.18.1
- [#775](https://github.com/Listenarrs/Listenarr/pull/775) - Bump axios from 1.17.0 to 1.18.1 in /fe
- [#782](https://github.com/Listenarrs/Listenarr/pull/782) - Bump shell-quote and concurrently in /fe
- [#793](https://github.com/Listenarrs/Listenarr/pull/793) - Bump postcss from 8.5.15 to 8.5.25 in /fe
- [#797](https://github.com/Listenarrs/Listenarr/pull/797) - Bump undici from 7.27.2 to 7.29.0 in /fe
- [#799](https://github.com/Listenarrs/Listenarr/pull/799) - Bump undici from 6.24.1 to 6.28.0 in /tools/discord-bot
- [#809](https://github.com/Listenarrs/Listenarr/pull/809) - Bump nanoid from 3.3.11 to 3.3.18 in /tools/discord-bot
- [#810](https://github.com/Listenarrs/Listenarr/pull/810) - Bump postcss from 8.5.10 to 8.5.26 in /tools/discord-bot
- [#811](https://github.com/Listenarrs/Listenarr/pull/811) - Bump nanoid from 3.3.12 to 3.3.18 in /fe
- [#813](https://github.com/Listenarrs/Listenarr/pull/813) - Bump brace-expansion in /fe
- [#931](https://github.com/Listenarrs/Listenarr/pull/931) - Bump sanitize-html from 2.17.4 to 2.17.7 in /tools/discord-bot
- [#932](https://github.com/Listenarrs/Listenarr/pull/932) - Bump postcss-selector-parser from 7.1.2 to 7.1.5 in /fe
- [#935](https://github.com/Listenarrs/Listenarr/pull/935) - Bump qs from 6.15.2 to 6.16.0 in /fe

Some existing targets are already below newer advisory floors: axios PRs target 1.18.1 while some alerts require 1.20.0; undici PRs target 6.28.0 / 7.29.0 while newer alerts require 6.29.1 / 7.30.1; sanitize-html targets 2.17.7 while newer alerts require 2.18.0. Recheck the actual PR diff before choosing an upgrade path.

## `package-lock.json` - 31 alerts

- [ ] [Alert #232](https://github.com/Listenarrs/Listenarr/security/dependabot/232) - `shell-quote`; **critical**; runtime; [GHSA-pqg4-j6r4-53mv](https://github.com/advisories/GHSA-pqg4-j6r4-53mv). Affected: `>= 1.8.4, < 1.11.0`. First patched: `1.11.0`.
- [ ] [Alert #130](https://github.com/Listenarrs/Listenarr/security/dependabot/130) - `axios`; **high**; runtime; [GHSA-gcfj-64vw-6mp9](https://github.com/advisories/GHSA-gcfj-64vw-6mp9). Affected: `>= 1.15.2, < 1.18.0`. First patched: `1.18.0`.
- [ ] [Alert #197](https://github.com/Listenarrs/Listenarr/security/dependabot/197) - `axios`; **high**; runtime; [GHSA-r4gj-5m52-g5wh](https://github.com/advisories/GHSA-r4gj-5m52-g5wh). Affected: `>= 1.17.0, < 1.20.0`. First patched: `1.20.0`.
- [ ] [Alert #199](https://github.com/Listenarrs/Listenarr/security/dependabot/199) - `axios`; **high**; runtime; [GHSA-c29m-xwm3-cm6r](https://github.com/advisories/GHSA-c29m-xwm3-cm6r). Affected: `>= 1.16.1, < 1.20.0`. First patched: `1.20.0`.
- [ ] [Alert #201](https://github.com/Listenarrs/Listenarr/security/dependabot/201) - `axios`; **high**; runtime; [GHSA-mghh-pgcx-3jjj](https://github.com/advisories/GHSA-mghh-pgcx-3jjj). Affected: `>= 1.15.0, < 1.20.0`. First patched: `1.20.0`.
- [ ] [Alert #203](https://github.com/Listenarrs/Listenarr/security/dependabot/203) - `axios`; **high**; runtime; [GHSA-x97p-jq2g-jp4f](https://github.com/advisories/GHSA-x97p-jq2g-jp4f). Affected: `>= 1.15.1, < 1.20.0`. First patched: `1.20.0`.
- [ ] [Alert #205](https://github.com/Listenarrs/Listenarr/security/dependabot/205) - `axios`; **high**; runtime; [GHSA-m8m8-qj5v-23w3](https://github.com/advisories/GHSA-m8m8-qj5v-23w3). Affected: `>= 1.15.2, < 1.20.0`. First patched: `1.20.0`.
- [ ] [Alert #225](https://github.com/Listenarrs/Listenarr/security/dependabot/225) - `axios`; **high**; runtime; [GHSA-542g-h47m-68v8](https://github.com/advisories/GHSA-542g-h47m-68v8). Affected: `>= 1.13.0, < 1.20.0`. First patched: `1.20.0`.
- [ ] [Alert #226](https://github.com/Listenarrs/Listenarr/security/dependabot/226) - `axios`; **high**; runtime; [GHSA-3pq3-5fj3-cg6v](https://github.com/advisories/GHSA-3pq3-5fj3-cg6v). Affected: `>= 1.13.0, < 1.20.0`. First patched: `1.20.0`.
- [ ] [Alert #240](https://github.com/Listenarrs/Listenarr/security/dependabot/240) - `follow-redirects`; **high**; runtime; [GHSA-8r9p-f939-6h3c](https://github.com/advisories/GHSA-8r9p-f939-6h3c). Affected: `>= 1.16.0, < 1.16.1`. First patched: `1.16.1`.
- [ ] [Alert #241](https://github.com/Listenarrs/Listenarr/security/dependabot/241) - `follow-redirects`; **high**; runtime; [GHSA-q3jh-wrc6-jgq8](https://github.com/advisories/GHSA-q3jh-wrc6-jgq8). Affected: `>= 1.16.0, < 1.16.1`. First patched: `1.16.1`.
- [ ] [Alert #112](https://github.com/Listenarrs/Listenarr/security/dependabot/112) - `form-data`; **high**; runtime; [GHSA-hmw2-7cc7-3qxx](https://github.com/advisories/GHSA-hmw2-7cc7-3qxx). Affected: `>= 4.0.0, < 4.0.6`. First patched: `4.0.6`.
- [ ] [Alert #183](https://github.com/Listenarrs/Listenarr/security/dependabot/183) - `joi`; **high**; runtime; [GHSA-6h2x-m376-mqjq](https://github.com/advisories/GHSA-6h2x-m376-mqjq). Affected: `>= 18.0.0, < 18.2.6`. First patched: `18.2.6`.
- [ ] [Alert #136](https://github.com/Listenarrs/Listenarr/security/dependabot/136) - `shell-quote`; **high**; runtime; [GHSA-395f-4hp3-45gv](https://github.com/advisories/GHSA-395f-4hp3-45gv). Affected: `<= 1.8.4`. First patched: `1.9.0`.
- [ ] [Alert #123](https://github.com/Listenarrs/Listenarr/security/dependabot/123) - `axios`; **medium**; runtime; [GHSA-xj6q-8x83-jv6g](https://github.com/advisories/GHSA-xj6q-8x83-jv6g). Affected: `>= 1.15.2, < 1.18.0`. First patched: `1.18.0`.
- [ ] [Alert #125](https://github.com/Listenarrs/Listenarr/security/dependabot/125) - `axios`; **medium**; runtime; [GHSA-f4gw-2p7v-4548](https://github.com/advisories/GHSA-f4gw-2p7v-4548). Affected: `>= 1.15.0, < 1.18.0`. First patched: `1.18.0`.
- [ ] [Alert #128](https://github.com/Listenarrs/Listenarr/security/dependabot/128) - `axios`; **medium**; runtime; [GHSA-hcpx-6fm6-wx23](https://github.com/advisories/GHSA-hcpx-6fm6-wx23). Affected: `>= 1.15.1, < 1.18.0`. First patched: `1.18.0`.
- [ ] [Alert #132](https://github.com/Listenarrs/Listenarr/security/dependabot/132) - `axios`; **medium**; runtime; [GHSA-mwf2-3pr3-8698](https://github.com/advisories/GHSA-mwf2-3pr3-8698). Affected: `>= 1.13.0, < 1.18.0`. First patched: `1.18.0`.
- [ ] [Alert #134](https://github.com/Listenarrs/Listenarr/security/dependabot/134) - `axios`; **medium**; runtime; [GHSA-jqh4-m9w3-8hp9](https://github.com/advisories/GHSA-jqh4-m9w3-8hp9). Affected: `>= 1.7.0, < 1.18.0`. First patched: `1.18.0`.
- [ ] [Alert #138](https://github.com/Listenarrs/Listenarr/security/dependabot/138) - `axios`; **medium**; runtime; [GHSA-pmv8-rq9r-6j72](https://github.com/advisories/GHSA-pmv8-rq9r-6j72). Affected: `>= 1.0.0, < 1.18.0`. First patched: `1.18.0`.
- [ ] [Alert #140](https://github.com/Listenarrs/Listenarr/security/dependabot/140) - `axios`; **medium**; runtime; [GHSA-42h9-826w-cgv3](https://github.com/advisories/GHSA-42h9-826w-cgv3). Affected: `>= 1.0.0, < 1.18.0`. First patched: `1.18.0`.
- [ ] [Alert #142](https://github.com/Listenarrs/Listenarr/security/dependabot/142) - `axios`; **medium**; runtime; [GHSA-7q8q-rj6j-mhjq](https://github.com/advisories/GHSA-7q8q-rj6j-mhjq). Affected: `>= 1.0.0, < 1.18.0`. First patched: `1.18.0`.
- [ ] [Alert #144](https://github.com/Listenarrs/Listenarr/security/dependabot/144) - `axios`; **medium**; runtime; [GHSA-mmx7-hfxf-jppx](https://github.com/advisories/GHSA-mmx7-hfxf-jppx). Affected: `>= 1.0.0, < 1.18.0`. First patched: `1.18.0`.
- [ ] [Alert #207](https://github.com/Listenarrs/Listenarr/security/dependabot/207) - `axios`; **medium**; runtime; [GHSA-44g4-m2mj-wpvx](https://github.com/advisories/GHSA-44g4-m2mj-wpvx). Affected: `>= 1.15.0, < 1.20.0`. First patched: `1.20.0`.
- [ ] [Alert #227](https://github.com/Listenarrs/Listenarr/security/dependabot/227) - `axios`; **medium**; runtime; [GHSA-9fr6-4gfg-395g](https://github.com/advisories/GHSA-9fr6-4gfg-395g). Affected: `>= 1.0.0, < 1.20.0`. First patched: `1.20.0`.
- [ ] [Alert #228](https://github.com/Listenarrs/Listenarr/security/dependabot/228) - `axios`; **medium**; runtime; [GHSA-vh66-26gq-q6x8](https://github.com/advisories/GHSA-vh66-26gq-q6x8). Affected: `>= 1.7.0, < 1.20.0`. First patched: `1.20.0`.
- [ ] [Alert #229](https://github.com/Listenarrs/Listenarr/security/dependabot/229) - `axios`; **medium**; runtime; [GHSA-4hqw-qxg8-jxx2](https://github.com/advisories/GHSA-4hqw-qxg8-jxx2). Affected: `>= 1.12.0, < 1.20.0`. First patched: `1.20.0`.
- [ ] [Alert #230](https://github.com/Listenarrs/Listenarr/security/dependabot/230) - `axios`; **medium**; runtime; [GHSA-j8rh-479h-cp32](https://github.com/advisories/GHSA-j8rh-479h-cp32). Affected: `>= 1.0.0, < 1.20.0`. First patched: `1.20.0`.
- [ ] [Alert #231](https://github.com/Listenarrs/Listenarr/security/dependabot/231) - `joi`; **medium**; runtime; [GHSA-wr44-6hxh-3jwq](https://github.com/advisories/GHSA-wr44-6hxh-3jwq). Affected: `>= 18.0.0, < 18.2.9`. First patched: `18.2.9`.
- [ ] [Alert #177](https://github.com/Listenarrs/Listenarr/security/dependabot/177) - `joi`; **low**; runtime; [GHSA-gg4h-3hg2-grpc](https://github.com/advisories/GHSA-gg4h-3hg2-grpc). Affected: `>= 18.0.0, < 18.2.4`. First patched: `18.2.4`.
- [ ] [Alert #179](https://github.com/Listenarrs/Listenarr/security/dependabot/179) - `joi`; **low**; runtime; [GHSA-6w3j-5fw6-r9vr](https://github.com/advisories/GHSA-6w3j-5fw6-r9vr). Affected: `>= 18.0.0, < 18.2.5`. First patched: `18.2.5`.

## `fe/package-lock.json` - 61 alerts

- [ ] [Alert #224](https://github.com/Listenarrs/Listenarr/security/dependabot/224) - `shell-quote`; **critical**; development; [GHSA-pqg4-j6r4-53mv](https://github.com/advisories/GHSA-pqg4-j6r4-53mv). Affected: `>= 1.8.4, < 1.11.0`. First patched: `1.11.0`.
- [ ] [Alert #220](https://github.com/Listenarrs/Listenarr/security/dependabot/220) - `@vue/server-renderer`; **high**; runtime; [GHSA-g2v6-rqmx-r4w6](https://github.com/advisories/GHSA-g2v6-rqmx-r4w6). Affected: `< 3.5.42`. First patched: `3.5.42`.
- [ ] [Alert #131](https://github.com/Listenarrs/Listenarr/security/dependabot/131) - `axios`; **high**; development; [GHSA-gcfj-64vw-6mp9](https://github.com/advisories/GHSA-gcfj-64vw-6mp9). Affected: `>= 1.15.2, < 1.18.0`. First patched: `1.18.0`.
- [ ] [Alert #198](https://github.com/Listenarrs/Listenarr/security/dependabot/198) - `axios`; **high**; development; [GHSA-r4gj-5m52-g5wh](https://github.com/advisories/GHSA-r4gj-5m52-g5wh). Affected: `>= 1.17.0, < 1.20.0`. First patched: `1.20.0`.
- [ ] [Alert #204](https://github.com/Listenarrs/Listenarr/security/dependabot/204) - `axios`; **high**; development; [GHSA-x97p-jq2g-jp4f](https://github.com/advisories/GHSA-x97p-jq2g-jp4f). Affected: `>= 1.15.1, < 1.20.0`. First patched: `1.20.0`.
- [ ] [Alert #206](https://github.com/Listenarrs/Listenarr/security/dependabot/206) - `axios`; **high**; development; [GHSA-m8m8-qj5v-23w3](https://github.com/advisories/GHSA-m8m8-qj5v-23w3). Affected: `>= 1.15.2, < 1.20.0`. First patched: `1.20.0`.
- [ ] [Alert #214](https://github.com/Listenarrs/Listenarr/security/dependabot/214) - `axios`; **high**; development; [GHSA-3pq3-5fj3-cg6v](https://github.com/advisories/GHSA-3pq3-5fj3-cg6v). Affected: `>= 1.13.0, < 1.20.0`. First patched: `1.20.0`.
- [ ] [Alert #127](https://github.com/Listenarrs/Listenarr/security/dependabot/127) - `brace-expansion`; **high**; development; [GHSA-3jxr-9vmj-r5cp](https://github.com/advisories/GHSA-3jxr-9vmj-r5cp). Affected: `>= 3.0.0, < 5.0.7`. First patched: `5.0.7`.
- [ ] [Alert #167](https://github.com/Listenarrs/Listenarr/security/dependabot/167) - `brace-expansion`; **high**; development; [GHSA-3jxr-9vmj-r5cp](https://github.com/advisories/GHSA-3jxr-9vmj-r5cp). Affected: `>= 2.0.0, < 2.1.2`. First patched: `2.1.2`.
- [ ] [Alert #235](https://github.com/Listenarrs/Listenarr/security/dependabot/235) - `follow-redirects`; **high**; development; [GHSA-8r9p-f939-6h3c](https://github.com/advisories/GHSA-8r9p-f939-6h3c). Affected: `>= 1.16.0, < 1.16.1`. First patched: `1.16.1`.
- [ ] [Alert #236](https://github.com/Listenarrs/Listenarr/security/dependabot/236) - `follow-redirects`; **high**; development; [GHSA-q3jh-wrc6-jgq8](https://github.com/advisories/GHSA-q3jh-wrc6-jgq8). Affected: `>= 1.16.0, < 1.16.1`. First patched: `1.16.1`.
- [ ] [Alert #111](https://github.com/Listenarrs/Listenarr/security/dependabot/111) - `form-data`; **high**; development; [GHSA-hmw2-7cc7-3qxx](https://github.com/advisories/GHSA-hmw2-7cc7-3qxx). Affected: `>= 4.0.0, < 4.0.6`. First patched: `4.0.6`.
- [ ] [Alert #165](https://github.com/Listenarrs/Listenarr/security/dependabot/165) - `nanoid`; **high**; runtime; [GHSA-28wg-ghj8-5hjv](https://github.com/advisories/GHSA-28wg-ghj8-5hjv). Affected: `< 3.3.16`. First patched: `3.3.16`.
- [ ] [Alert #170](https://github.com/Listenarrs/Listenarr/security/dependabot/170) - `nanoid`; **high**; runtime; [GHSA-2v37-7h3g-55p8](https://github.com/advisories/GHSA-2v37-7h3g-55p8). Affected: `< 3.3.18`. First patched: `3.3.18`.
- [ ] [Alert #150](https://github.com/Listenarrs/Listenarr/security/dependabot/150) - `postcss`; **high**; runtime; [GHSA-r28c-9q8g-f849](https://github.com/advisories/GHSA-r28c-9q8g-f849). Affected: `<= 8.5.17`. First patched: `8.5.18`.
- [ ] [Alert #137](https://github.com/Listenarrs/Listenarr/security/dependabot/137) - `shell-quote`; **high**; development; [GHSA-395f-4hp3-45gv](https://github.com/advisories/GHSA-395f-4hp3-45gv). Affected: `<= 1.8.4`. First patched: `1.9.0`.
- [ ] [Alert #223](https://github.com/Listenarrs/Listenarr/security/dependabot/223) - `source-map-js`; **high**; runtime; [GHSA-68fv-2mgg-jv7q](https://github.com/advisories/GHSA-68fv-2mgg-jv7q). Affected: `>= 1.0.0, < 1.2.2`. First patched: `1.2.2`.
- [ ] [Alert #109](https://github.com/Listenarrs/Listenarr/security/dependabot/109) - `undici`; **high**; development; [GHSA-vmh5-mc38-953g](https://github.com/advisories/GHSA-vmh5-mc38-953g). Affected: `>= 7.23.0, < 7.28.0`. First patched: `7.28.0`.
- [ ] [Alert #113](https://github.com/Listenarrs/Listenarr/security/dependabot/113) - `undici`; **high**; development; [GHSA-hm92-r4w5-c3mj](https://github.com/advisories/GHSA-hm92-r4w5-c3mj). Affected: `>= 7.23.0, < 7.28.0`. First patched: `7.28.0`.
- [ ] [Alert #153](https://github.com/Listenarrs/Listenarr/security/dependabot/153) - `undici`; **high**; development; [GHSA-4cwx-7wf7-3272](https://github.com/advisories/GHSA-4cwx-7wf7-3272). Affected: `>= 7.0.0, < 7.29.0`. First patched: `7.29.0`.
- [ ] [Alert #185](https://github.com/Listenarrs/Listenarr/security/dependabot/185) - `undici`; **high**; development; [GHSA-w293-vg96-wgc3](https://github.com/advisories/GHSA-w293-vg96-wgc3). Affected: `>= 7.24.1, < 7.29.1`. First patched: `7.29.1`.
- [ ] [Alert #249](https://github.com/Listenarrs/Listenarr/security/dependabot/249) - `undici`; **high**; development; [GHSA-xjj6-f33q-929c](https://github.com/advisories/GHSA-xjj6-f33q-929c). Affected: `>= 7.18.0, < 7.30.1`. First patched: `7.30.1`.
- [ ] [Alert #238](https://github.com/Listenarrs/Listenarr/security/dependabot/238) - `vite`; **high**; development; [GHSA-587h-7wmj-h5pm](https://github.com/advisories/GHSA-587h-7wmj-h5pm). Affected: `>= 8.0.0, <= 8.1.5`. First patched: `8.1.6`.
- [ ] [Alert #181](https://github.com/Listenarrs/Listenarr/security/dependabot/181) - `@vitest/mocker`; **medium**; development; [GHSA-82fw-gwwq-j7x9](https://github.com/advisories/GHSA-82fw-gwwq-j7x9). Affected: `>= 2.1.0, < 4.1.11`. First patched: `4.1.11`.
- [ ] [Alert #246](https://github.com/Listenarrs/Listenarr/security/dependabot/246) - `@vitest/mocker`; **medium**; development; [GHSA-qxg6-89mg-rqv8](https://github.com/advisories/GHSA-qxg6-89mg-rqv8). Affected: `>= 2.1.0-beta.7, < 4.1.12`. First patched: `4.1.12`.
- [ ] [Alert #124](https://github.com/Listenarrs/Listenarr/security/dependabot/124) - `axios`; **medium**; development; [GHSA-xj6q-8x83-jv6g](https://github.com/advisories/GHSA-xj6q-8x83-jv6g). Affected: `>= 1.15.2, < 1.18.0`. First patched: `1.18.0`.
- [ ] [Alert #126](https://github.com/Listenarrs/Listenarr/security/dependabot/126) - `axios`; **medium**; development; [GHSA-f4gw-2p7v-4548](https://github.com/advisories/GHSA-f4gw-2p7v-4548). Affected: `>= 1.15.0, < 1.18.0`. First patched: `1.18.0`.
- [ ] [Alert #143](https://github.com/Listenarrs/Listenarr/security/dependabot/143) - `axios`; **medium**; development; [GHSA-7q8q-rj6j-mhjq](https://github.com/advisories/GHSA-7q8q-rj6j-mhjq). Affected: `>= 1.0.0, < 1.18.0`. First patched: `1.18.0`.
- [ ] [Alert #145](https://github.com/Listenarrs/Listenarr/security/dependabot/145) - `axios`; **medium**; development; [GHSA-mmx7-hfxf-jppx](https://github.com/advisories/GHSA-mmx7-hfxf-jppx). Affected: `>= 1.0.0, < 1.18.0`. First patched: `1.18.0`.
- [ ] [Alert #208](https://github.com/Listenarrs/Listenarr/security/dependabot/208) - `axios`; **medium**; development; [GHSA-44g4-m2mj-wpvx](https://github.com/advisories/GHSA-44g4-m2mj-wpvx). Affected: `>= 1.15.0, < 1.20.0`. First patched: `1.20.0`.
- [ ] [Alert #215](https://github.com/Listenarrs/Listenarr/security/dependabot/215) - `axios`; **medium**; development; [GHSA-9fr6-4gfg-395g](https://github.com/advisories/GHSA-9fr6-4gfg-395g). Affected: `>= 1.0.0, < 1.20.0`. First patched: `1.20.0`.
- [ ] [Alert #216](https://github.com/Listenarrs/Listenarr/security/dependabot/216) - `axios`; **medium**; development; [GHSA-vh66-26gq-q6x8](https://github.com/advisories/GHSA-vh66-26gq-q6x8). Affected: `>= 1.7.0, < 1.20.0`. First patched: `1.20.0`.
- [ ] [Alert #217](https://github.com/Listenarrs/Listenarr/security/dependabot/217) - `axios`; **medium**; development; [GHSA-4hqw-qxg8-jxx2](https://github.com/advisories/GHSA-4hqw-qxg8-jxx2). Affected: `>= 1.12.0, < 1.20.0`. First patched: `1.20.0`.
- [ ] [Alert #218](https://github.com/Listenarrs/Listenarr/security/dependabot/218) - `axios`; **medium**; development; [GHSA-j8rh-479h-cp32](https://github.com/advisories/GHSA-j8rh-479h-cp32). Affected: `>= 1.0.0, < 1.20.0`. First patched: `1.20.0`.
- [ ] [Alert #211](https://github.com/Listenarrs/Listenarr/security/dependabot/211) - `brace-expansion`; **medium**; development; [GHSA-q2hr-2g5m-vwhr](https://github.com/advisories/GHSA-q2hr-2g5m-vwhr). Affected: `>= 4.0.0, < 5.0.12`. First patched: `5.0.12`.
- [ ] [Alert #212](https://github.com/Listenarrs/Listenarr/security/dependabot/212) - `brace-expansion`; **medium**; development; [GHSA-q2hr-2g5m-vwhr](https://github.com/advisories/GHSA-q2hr-2g5m-vwhr). Affected: `>= 2.0.0, < 2.1.7`. First patched: `2.1.7`.
- [ ] [Alert #222](https://github.com/Listenarrs/Listenarr/security/dependabot/222) - `joi`; **medium**; development; [GHSA-wr44-6hxh-3jwq](https://github.com/advisories/GHSA-wr44-6hxh-3jwq). Affected: `>= 18.0.0, < 18.2.9`. First patched: `18.2.9`.
- [ ] [Alert #166](https://github.com/Listenarrs/Listenarr/security/dependabot/166) - `postcss`; **medium**; runtime; [GHSA-fxqj-rqcc-2cmp](https://github.com/advisories/GHSA-fxqj-rqcc-2cmp). Affected: `<= 8.5.22`. First patched: `8.5.23`.
- [ ] [Alert #173](https://github.com/Listenarrs/Listenarr/security/dependabot/173) - `qs`; **medium**; runtime; [GHSA-x5fp-wj9c-mxmx](https://github.com/advisories/GHSA-x5fp-wj9c-mxmx). Affected: `>= 6.14.2, <= 6.15.3`. First patched: `6.16.0`.
- [ ] [Alert #176](https://github.com/Listenarrs/Listenarr/security/dependabot/176) - `qs`; **medium**; runtime; [GHSA-4mjr-xmp4-gh2g](https://github.com/advisories/GHSA-4mjr-xmp4-gh2g). Affected: `>= 2.2.5, < 6.16.0`. First patched: `6.16.0`.
- [ ] [Alert #110](https://github.com/Listenarrs/Listenarr/security/dependabot/110) - `undici`; **medium**; development; [GHSA-pr7r-676h-xcf6](https://github.com/advisories/GHSA-pr7r-676h-xcf6). Affected: `>= 7.0.0, < 7.28.0`. First patched: `7.28.0`.
- [ ] [Alert #115](https://github.com/Listenarrs/Listenarr/security/dependabot/115) - `undici`; **medium**; development; [GHSA-p88m-4jfj-68fv](https://github.com/advisories/GHSA-p88m-4jfj-68fv). Affected: `>= 7.0.0, < 7.28.0`. First patched: `7.28.0`.
- [ ] [Alert #154](https://github.com/Listenarrs/Listenarr/security/dependabot/154) - `undici`; **medium**; development; [GHSA-8xcm-r25x-g524](https://github.com/advisories/GHSA-8xcm-r25x-g524). Affected: `>= 7.0.0, < 7.29.0`. First patched: `7.29.0`.
- [ ] [Alert #155](https://github.com/Listenarrs/Listenarr/security/dependabot/155) - `undici`; **medium**; development; [GHSA-v3r7-h72x-cjcm](https://github.com/advisories/GHSA-v3r7-h72x-cjcm). Affected: `>= 7.0.0, < 7.29.0`. First patched: `7.29.0`.
- [ ] [Alert #156](https://github.com/Listenarrs/Listenarr/security/dependabot/156) - `undici`; **medium**; development; [GHSA-jr45-8vmc-qm54](https://github.com/advisories/GHSA-jr45-8vmc-qm54). Affected: `>= 7.0.0, < 7.29.0`. First patched: `7.29.0`.
- [ ] [Alert #157](https://github.com/Listenarrs/Listenarr/security/dependabot/157) - `undici`; **medium**; development; [GHSA-m8rv-5g2x-5cg5](https://github.com/advisories/GHSA-m8rv-5g2x-5cg5). Affected: `>= 7.0.0, < 7.29.0`. First patched: `7.29.0`.
- [ ] [Alert #187](https://github.com/Listenarrs/Listenarr/security/dependabot/187) - `undici`; **medium**; development; [GHSA-pmjh-fq2x-6v4x](https://github.com/advisories/GHSA-pmjh-fq2x-6v4x). Affected: `>= 7.11.0, < 7.29.1`. First patched: `7.29.1`.
- [ ] [Alert #192](https://github.com/Listenarrs/Listenarr/security/dependabot/192) - `undici`; **medium**; development; [GHSA-2jfj-6hjv-fm6j](https://github.com/advisories/GHSA-2jfj-6hjv-fm6j). Affected: `>= 7.0.0, < 7.29.1`. First patched: `7.29.1`.
- [ ] [Alert #237](https://github.com/Listenarrs/Listenarr/security/dependabot/237) - `vite`; **medium**; development; [GHSA-698v-4r3x-3vcv](https://github.com/advisories/GHSA-698v-4r3x-3vcv). Affected: `>= 8.0.0, <= 8.1.5`. First patched: `8.1.6`.
- [ ] [Alert #239](https://github.com/Listenarrs/Listenarr/security/dependabot/239) - `vite`; **medium**; development; [GHSA-r825-w8mm-5pxw](https://github.com/advisories/GHSA-r825-w8mm-5pxw). Affected: `>= 8.0.0, <= 8.1.5`. First patched: `8.1.6`.
- [ ] [Alert #244](https://github.com/Listenarrs/Listenarr/security/dependabot/244) - `vite`; **medium**; development; [GHSA-rq7h-c2jc-7f22](https://github.com/advisories/GHSA-rq7h-c2jc-7f22). Affected: `>= 8.0.0, <= 8.1.5`. First patched: `8.1.6`.
- [ ] [Alert #182](https://github.com/Listenarrs/Listenarr/security/dependabot/182) - `vitest`; **medium**; development; [GHSA-82fw-gwwq-j7x9](https://github.com/advisories/GHSA-82fw-gwwq-j7x9). Affected: `>= 2.1.0, < 4.1.11`. First patched: `4.1.11`.
- [ ] [Alert #178](https://github.com/Listenarrs/Listenarr/security/dependabot/178) - `joi`; **low**; development; [GHSA-gg4h-3hg2-grpc](https://github.com/advisories/GHSA-gg4h-3hg2-grpc). Affected: `>= 18.0.0, < 18.2.4`. First patched: `18.2.4`.
- [ ] [Alert #180](https://github.com/Listenarrs/Listenarr/security/dependabot/180) - `joi`; **low**; development; [GHSA-6w3j-5fw6-r9vr](https://github.com/advisories/GHSA-6w3j-5fw6-r9vr). Affected: `>= 18.0.0, < 18.2.5`. First patched: `18.2.5`.
- [ ] [Alert #172](https://github.com/Listenarrs/Listenarr/security/dependabot/172) - `postcss-selector-parser`; **low**; development; [GHSA-w9m9-85wc-3x92](https://github.com/advisories/GHSA-w9m9-85wc-3x92). Affected: `>= 7.1.0, < 7.1.3`. First patched: `7.1.3`.
- [ ] [Alert #114](https://github.com/Listenarrs/Listenarr/security/dependabot/114) - `undici`; **low**; development; [GHSA-35p6-xmwp-9g52](https://github.com/advisories/GHSA-35p6-xmwp-9g52). Affected: `>= 7.0.0, < 7.28.0`. First patched: `7.28.0`.
- [ ] [Alert #117](https://github.com/Listenarrs/Listenarr/security/dependabot/117) - `undici`; **low**; development; [GHSA-g8m3-5g58-fq7m](https://github.com/advisories/GHSA-g8m3-5g58-fq7m). Affected: `>= 7.0.0, < 7.28.0`. First patched: `7.28.0`.
- [ ] [Alert #188](https://github.com/Listenarrs/Listenarr/security/dependabot/188) - `undici`; **low**; development; [GHSA-2gqq-gqf2-x968](https://github.com/advisories/GHSA-2gqq-gqf2-x968). Affected: `>= 7.1.0, < 7.29.1`. First patched: `7.29.1`.
- [ ] [Alert #191](https://github.com/Listenarrs/Listenarr/security/dependabot/191) - `undici`; **low**; development; [GHSA-r53p-7pc4-xj5r](https://github.com/advisories/GHSA-r53p-7pc4-xj5r). Affected: `>= 7.0.0, < 7.29.1`. First patched: `7.29.1`.
- [ ] [Alert #193](https://github.com/Listenarrs/Listenarr/security/dependabot/193) - `undici`; **low**; development; [GHSA-8436-99hf-9mmv](https://github.com/advisories/GHSA-8436-99hf-9mmv). Affected: `>= 7.0.0, < 7.29.1`. First patched: `7.29.1`.
- [ ] [Alert #245](https://github.com/Listenarrs/Listenarr/security/dependabot/245) - `vite`; **low**; development; [GHSA-9jrq-w75r-8gcw](https://github.com/advisories/GHSA-9jrq-w75r-8gcw). Affected: `>= 8.0.0, <= 8.1.5`. First patched: `8.1.6`.

## `tools/discord-bot/package-lock.json` - 23 alerts

- [ ] [Alert #162](https://github.com/Listenarrs/Listenarr/security/dependabot/162) - `nanoid`; **high**; runtime; [GHSA-28wg-ghj8-5hjv](https://github.com/advisories/GHSA-28wg-ghj8-5hjv). Affected: `< 3.3.16`. First patched: `3.3.16`.
- [ ] [Alert #169](https://github.com/Listenarrs/Listenarr/security/dependabot/169) - `nanoid`; **high**; runtime; [GHSA-2v37-7h3g-55p8](https://github.com/advisories/GHSA-2v37-7h3g-55p8). Affected: `< 3.3.18`. First patched: `3.3.18`.
- [ ] [Alert #175](https://github.com/Listenarrs/Listenarr/security/dependabot/175) - `nanoid`; **high**; runtime; [GHSA-xwg4-73v4-xw9w](https://github.com/advisories/GHSA-xwg4-73v4-xw9w). Affected: `< 3.3.12`. First patched: `3.3.12`.
- [ ] [Alert #146](https://github.com/Listenarrs/Listenarr/security/dependabot/146) - `postcss`; **high**; runtime; [GHSA-6g55-p6wh-862q](https://github.com/advisories/GHSA-6g55-p6wh-862q). Affected: `<= 8.5.11`. First patched: `8.5.12`.
- [ ] [Alert #149](https://github.com/Listenarrs/Listenarr/security/dependabot/149) - `postcss`; **high**; runtime; [GHSA-r28c-9q8g-f849](https://github.com/advisories/GHSA-r28c-9q8g-f849). Affected: `<= 8.5.17`. First patched: `8.5.18`.
- [ ] [Alert #234](https://github.com/Listenarrs/Listenarr/security/dependabot/234) - `source-map-js`; **high**; runtime; [GHSA-68fv-2mgg-jv7q](https://github.com/advisories/GHSA-68fv-2mgg-jv7q). Affected: `>= 1.0.0, < 1.2.2`. First patched: `1.2.2`.
- [ ] [Alert #120](https://github.com/Listenarrs/Listenarr/security/dependabot/120) - `undici`; **high**; runtime; [GHSA-vxpw-j846-p89q](https://github.com/advisories/GHSA-vxpw-j846-p89q). Affected: `< 6.27.0`. First patched: `6.27.0`.
- [ ] [Alert #194](https://github.com/Listenarrs/Listenarr/security/dependabot/194) - `undici`; **high**; runtime; [GHSA-rfgv-xxqx-mfg5](https://github.com/advisories/GHSA-rfgv-xxqx-mfg5). Affected: `>= 6.7.0, < 6.28.1`. First patched: `6.28.1`.
- [ ] [Alert #251](https://github.com/Listenarrs/Listenarr/security/dependabot/251) - `undici`; **high**; runtime; [GHSA-xjj6-f33q-929c](https://github.com/advisories/GHSA-xjj6-f33q-929c). Affected: `>= 6.23.0, < 6.29.1`. First patched: `6.29.1`.
- [ ] [Alert #163](https://github.com/Listenarrs/Listenarr/security/dependabot/163) - `postcss`; **medium**; runtime; [GHSA-fxqj-rqcc-2cmp](https://github.com/advisories/GHSA-fxqj-rqcc-2cmp). Affected: `<= 8.5.22`. First patched: `8.5.23`.
- [ ] [Alert #147](https://github.com/Listenarrs/Listenarr/security/dependabot/147) - `sanitize-html`; **medium**; runtime; [GHSA-vccv-cmxp-4j9h](https://github.com/advisories/GHSA-vccv-cmxp-4j9h). Affected: `>= 1.18.0, <= 2.17.4`. First patched: `2.17.5`.
- [ ] [Alert #171](https://github.com/Listenarrs/Listenarr/security/dependabot/171) - `sanitize-html`; **medium**; runtime; [GHSA-g8qq-57p8-ggw5](https://github.com/advisories/GHSA-g8qq-57p8-ggw5). Affected: `>= 1.9.0, <= 2.17.6`. First patched: `2.17.7`.
- [ ] [Alert #174](https://github.com/Listenarrs/Listenarr/security/dependabot/174) - `sanitize-html`; **medium**; runtime; [GHSA-jxwj-j7wr-gfrw](https://github.com/advisories/GHSA-jxwj-j7wr-gfrw). Affected: `<= 2.17.5`. First patched: `2.17.6`.
- [ ] [Alert #242](https://github.com/Listenarrs/Listenarr/security/dependabot/242) - `sanitize-html`; **medium**; runtime; [GHSA-x3q4-9hxx-gx8m](https://github.com/advisories/GHSA-x3q4-9hxx-gx8m). Affected: `< 2.18.0`. First patched: `2.18.0`.
- [ ] [Alert #243](https://github.com/Listenarrs/Listenarr/security/dependabot/243) - `sanitize-html`; **medium**; runtime; [GHSA-cv27-6wvh-8x7j](https://github.com/advisories/GHSA-cv27-6wvh-8x7j). Affected: `< 2.18.0`. First patched: `2.18.0`.
- [ ] [Alert #121](https://github.com/Listenarrs/Listenarr/security/dependabot/121) - `undici`; **medium**; runtime; [GHSA-p88m-4jfj-68fv](https://github.com/advisories/GHSA-p88m-4jfj-68fv). Affected: `< 6.27.0`. First patched: `6.27.0`.
- [ ] [Alert #158](https://github.com/Listenarrs/Listenarr/security/dependabot/158) - `undici`; **medium**; runtime; [GHSA-m8rv-5g2x-5cg5](https://github.com/advisories/GHSA-m8rv-5g2x-5cg5). Affected: `< 6.28.0`. First patched: `6.28.0`.
- [ ] [Alert #159](https://github.com/Listenarrs/Listenarr/security/dependabot/159) - `undici`; **medium**; runtime; [GHSA-8xcm-r25x-g524](https://github.com/advisories/GHSA-8xcm-r25x-g524). Affected: `< 6.28.0`. First patched: `6.28.0`.
- [ ] [Alert #160](https://github.com/Listenarrs/Listenarr/security/dependabot/160) - `undici`; **medium**; runtime; [GHSA-v3r7-h72x-cjcm](https://github.com/advisories/GHSA-v3r7-h72x-cjcm). Affected: `< 6.28.0`. First patched: `6.28.0`.
- [ ] [Alert #250](https://github.com/Listenarrs/Listenarr/security/dependabot/250) - `undici`; **medium**; runtime; [GHSA-4qv8-2p9j-2685](https://github.com/advisories/GHSA-4qv8-2p9j-2685). Affected: `>= 6.5.0, < 6.29.1`. First patched: `6.29.1`.
- [ ] [Alert #119](https://github.com/Listenarrs/Listenarr/security/dependabot/119) - `undici`; **low**; runtime; [GHSA-35p6-xmwp-9g52](https://github.com/advisories/GHSA-35p6-xmwp-9g52). Affected: `< 6.27.0`. First patched: `6.27.0`.
- [ ] [Alert #122](https://github.com/Listenarrs/Listenarr/security/dependabot/122) - `undici`; **low**; runtime; [GHSA-g8m3-5g58-fq7m](https://github.com/advisories/GHSA-g8m3-5g58-fq7m). Affected: `< 6.27.0`. First patched: `6.27.0`.
- [ ] [Alert #233](https://github.com/Listenarrs/Listenarr/security/dependabot/233) - `undici`; **low**; runtime; [GHSA-r53p-7pc4-xj5r](https://github.com/advisories/GHSA-r53p-7pc4-xj5r). Affected: `< 6.28.1`. First patched: `6.28.1`.

## Completion criteria

Every alert above has a linked merged fix or a documented, reviewed disposition; the complete live Dependabot inventory has been reconciled; clean installs and relevant checks pass for each changed project. Keep the tracking issue open until remediation is complete. The initial documentation PR does not close this issue.
