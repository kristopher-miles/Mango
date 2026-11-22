# Serving boundary rollout

[Mango v0.0.23](https://github.com/kristopher-miles/Mango/releases/tag/v0.0.23) is running on all five enrolled nodes. A simultaneous pair submitted to NucBox generated on both workers for 36.104 s. With GamingDesktop offline, two requests submitted directly to Luna both completed, with the second waiting 71.830 s. When GamingDesktop was available for inference again, a fresh direct-to-Luna pair generated on both workers for 54.559 s. The [interruption results](#repeated-gamingdesktop-interruption-with-the-corrected-server) distinguish the first listener response from readiness to serve.

## First signed candidate

The first Windows canary, v0.0.21, was built from commit `d39298ffe957a26d9e9fb75e613e15fc4509699e`. Protocol window 1 is unchanged. Each package has its own RSA-PSS/SHA-256 signed manifest.

| Platform | Package bytes | SHA-256 |
| --- | ---: | --- |
| Windows x64 | 64,762,254 | `ff10fd231c72cec9356bc08943a5e050181a19b2791f4d99499117abf5d16225` |
| Linux x64 | 50,697,682 | `5a75238f4e5529cb11c8dab0d30c02cedb0f488b936209a1f6ad97c0a11691c1` |
| Linux arm64 | 47,891,927 | `4941fe6345a8ac1bcee6c5f1fd4828caf1311de7f327e21c67406f7a81d5b84d` |

## Qualification

The pinned .NET 10.0.404 Release build, full host suite and 22 web UI tests passed on NucBox. Focused tests covered checked pool accounting, mixed and missing estimate inputs, withdrawal migration, concurrent YOLO loads, native refusals, settlement, scheduling and staggered SSE generation on distinct workers. Two isolated Windows processes using the actual signed v0.0.20 and v0.0.21 packages joined a fresh network. Each ingress forwarded chat to the other package's fixture exactly once. The requests were `246b87aa-c2bf-4c0a-b8cf-54d6ee149a75` and `426d9c70-8326-4452-860a-10c93c701c5a`.

The signed Windows package ran for 901.8 seconds with 20 peers and 100 deployments. Its idle CPU was 0.2686% of one core, peak resident memory was 133.2 MiB, and it made zero peer HTTP calls. Across 100 warmed requests HTTP p95 was 1.986 ms, including 1.485 ms outside the provider envelope. One shared edit produced 19 delta pushes. Two consecutive missing-model requests shared one discovery round.

Both Linux x64 smoke scripts exited 0 on star-server-mini after local signature and digest verification. The leech smoke used a fresh private data directory. The managed updater fixture exercised an upgrade and fallback. No arm64 host was enrolled.

## Enrolled hosts

| Node | Baseline | Baseline verification |
| --- | --- | --- |
| GamingDesktop | Windows x64 0.0.20, contributor | Managed Strata, one slot, 200,000 context. A synthetic warm request completed in 0.65 s on the local native worker. Verified the signed rollback ZIP and nine encrypted configuration snapshots. |
| luna-station | Windows x64 0.0.20, contributor | Managed `qwen3.8-flash-next-strata-256k-vision`, one slot, 262,144 context. Twelve owner-restricted DPAPI backup files and the signed 0.0.20 rollback ZIP were verified. Daily updates on and maintenance deferral off. |
| Nuc-eGPU-box | Windows x64 0.0.20, contributor | Local LM Studio and Mango each returned 768 embedding values. Native inventory confirmed two small local chat models resident alongside the embedding model at 2,048 context. After the test the embedding model was the sole resident. The linked Mac instances remained disabled. |
| NucBox_M6Ultra | Windows x64 0.0.19 preview, leech | The local host retained 11 verified DPAPI configuration backups and both signed 0.0.19 and 0.0.20 Windows packages. Daily updates off and maintenance deferral off. |
| star-server-mini | Linux x64 0.0.19 preview, leech | The managed systemd service ran as `mango`. The authenticated Applebot relay served chat and a 768-dimensional `text-embedding-nomic-embed-text-v2-moe` vector. Daily updates on and maintenance deferral off. |

## First Windows cutover

NucBox ran the signed 0.0.21 binary from `%LOCALAPPDATA%\Programs\Mango-v0.0.21`. Its prior preview remains at `%LOCALAPPDATA%\Mango-browser-codes-preview`. After a graceful stop, the owner took a fresh encrypted snapshot. The restarted host retained its network and node identities, all four persisted inference-key identities and its leech role. The local settings, provider and public ingress files matched the snapshots byte for byte. The authenticated local dashboard and tailnet listener both reported the new version.

## Baseline and first Windows canary

At 00:05:12 UTC on November 22, NucBox's 0.0.19 ingress sent a long synthetic `flash-next` stream to GamingDesktop on 0.0.20. Request `a244ab96-d1bb-441e-90a6-5a65a41475cd` had one attempt, `504b943b-c13a-48b6-b976-f658b550ae7e`. Mango recorded 40.349 seconds loading and 299.818 seconds queued. The client reached its 300-second deadline before receiving any output. The ingress recorded cancellation with HTTP 503. The second stream was conditional on first output and never started.

On 0.0.21, GamingDesktop served the cold and warm canaries on `qwen3.8-flash-next-iq2_xs`. The cold request `aa3f6257-bf4b-4fd4-b980-28cd6c6068e9`, attempt `2c8d27c8-b26b-469e-b492-61cb93ff5aa0`, started unloaded. The native start took 25.122 seconds, first output arrived at 28.237 seconds and the client saw the final marker at 29.235 seconds. That client closed at the marker and the journal recorded cancellation. The independent warm request `77021459-2412-4d1d-81b9-da2ae09fe24e`, attempt `c2fc3da1-9948-45a0-8190-d1d6dbef00c7`, processed 8,078 input tokens, emitted first output at 4.067 seconds and completed at 5.149 seconds. It drained to EOF and journaled HTTP 200. Mango recorded 117.5 tokens/s and zero extra execution RAM or device bytes.

GamingDesktop repeated a naturally cold request and consumed the stream through HTTP EOF. Request `9bb58c49-79b4-4c71-942f-50ad247620f8`, attempt `7a2d4c45-4a10-44d3-ad79-26211905fe4f`, completed with HTTP 200 in both Mango records. Strata's native startup took 36.171 seconds and generation ran at 91.6 tokens/s.

## Simultaneous requests to the same alias

Two independent HTTP clients submitted prepared bodies for `flash-next` from a single barrier, over separate, already-open TCP connections. The table uses the actual ingress timestamps. Generation overlap runs from the later first generated output to the earlier last generated output. Each client consumed its stream through HTTP EOF.

| Ingress | Ingress skew | GamingDesktop attempt | Luna attempt | Generation overlap (UTC) | Result |
| --- | ---: | --- | --- | --- | --- |
| luna-station, local `flash-next` worker | 0.6 µs | `fbdc6352-50ef-4585-a980-34ff37993fe4` | `b2ae6817-3d21-4015-a6a0-42772001c33f` | 00:42:35.297–00:43:14.575, 39.278 s | Both HTTP 200, one attempt each, 6,144 output tokens each. |
| NucBox_M6Ultra, no local model | 2.8 µs | `dbe1ddee-e38a-406c-8ac2-91e8f1e7116a` | `ecaf2b49-6262-4a57-a463-2e03df707c16` | 00:40:27.344–00:41:11.011, 43.667 s | Both HTTP 200, one attempt each, 6,144 output tokens each. |

The NucBox clients began sending 52.3 µs apart. NucBox assigned the two attempts 3.2 µs apart. Live scheduler observations showed both attempts running with output already started. The request IDs were `53455c43-d490-44ef-afab-15fe240cebe7` (GamingDesktop) and `1f156194-b9f7-47d7-9542-fd6aeb5e6c77` (Luna). The inputs contained 31,382 and 31,381 native tokens respectively.

GamingDesktop's serving-host record showed 0.055 ms queued, 11.422 seconds to first token and 140.9 tokens/s for this simultaneous pair. The separate staggered NucBox pair started its second client 21 ms after the first client observed generated text. The Luna and GamingDesktop attempts were `9d323021-86f2-4d72-808c-e28eff8afaed` and `fae61536-db5f-411d-8f7c-08760dd5edf0`. Both returned HTTP 200, generated 6,144 tokens and overlapped from 00:44:46.382 to 00:45:23.164 UTC (36.782 seconds).

Luna's clients began sending 36.6 µs apart. Its local request `a0921148-7819-4429-ba5e-d8d4fe3bbf29` queued for 0.0796 ms. Luna forwarded request `5f142db4-587e-462d-9cd0-e4f8dd8e68e2` to GamingDesktop. In the earlier NucBox pair, Luna's serving-host record measured 0.4132 ms queued. The first diagnostic attempt sourced connections from 127.0.0.2 and 127.0.0.3. The owner-session listener rejected those with HTTP 403 before allocating inference. Both successful runs used independent connections on 127.0.0.1.

## Native residency on Nuc-eGPU-box

On 0.0.21, cold Gemma loaded in 10.50 seconds while Nemotron generated. Its first output arrived after 11.50 seconds, with Nemotron still generating for another 11.96 seconds. Submission of the ordinary embedding during YOLO group execution left both chats running until completion. The embedding then became the sole resident, returning output 12.11 seconds after the last chat finished. On the reverse switch only Nemotron loaded, with first output at 6.00 seconds. In checked mode, the 6,545 MiB measured load peak allowed both chat models to reside together. A deliberately oversized override instead kept Gemma cold until Nemotron finished and unloaded, then Gemma loaded in 6.60 seconds. The host restored the original flags, removed its temporary override, and returned its original 512-context embedding to sole residency. Direct LM Studio and Mango again both returned 768 dimensions.


On Nuc-eGPU-box the YOLO-group-to-ordinary attempt was `ce15a4d1-0722-46ba-9d78-5eb3049c6263`, the reverse attempt was `d2b398b6-411f-4c3f-9527-1bbcdd502514`, the checked-fit attempt was `90e44f05-906f-454c-a6bc-800298e9b782` and the insufficient-fit attempt was `ec84e6fb-0b60-437c-b32b-451a9b2cd9cf`. In the first manually primed run, the client sent one request before inventory had caught up and a second native Nemotron instance appeared. A repeat waited for the native and Mango instance IDs to agree. The same sole Nemotron instance then stayed active throughout the cold Gemma load.

## Listener outage and fresh requests after recovery

GamingDesktop had zero active or waiting requests when its Mango process was terminated without a graceful peer withdrawal at 00:57:51.267 UTC on November 22. Orbit and Tailscale stayed up. Two previously connected clients released their independent requests against Luna's own ingress at a single barrier. The client send skew was 36.5 µs. All 239 reachability samples taken on Luna during the run found GamingDesktop unavailable.

Both requests completed serially on Luna, returned HTTP 200, emitted the stream terminator and drained to EOF. One waited 0.0665 ms and the other 51,250.8635 ms. A stale placement initially selected GamingDesktop and failed to connect. Its internal 503 was recoverable under the original request deadline. Request `fe10c65d-8e4f-4bad-afcc-10ce5dd7ac2b` ran on Luna as attempt `7c5e0a77-c35d-4752-9b90-98765e7889e8`. Request `1d729052-788c-49a9-a967-cdf58a4035df` first tried GamingDesktop (`86379bcc-1ec0-4c57-914e-4927b8cede00`) and completed on Luna (`1587ef8c-4efd-453a-9c37-66949a8f5158`). GamingDesktop restarted at 01:02:14.889 UTC with its original identity and its model cold.

The first fresh pair on v0.0.21 arrived at Luna 0.5 µs apart. Both requests completed, but their generation did not overlap. The second request (`fcd875a2-2230-4098-8eac-ffe382f76fa7`) went to GamingDesktop, attempt `81b32d2f-6180-4906-a03f-3f57b6328c8c`. Strata operation `22f466ad8d534b6c8e84c97b859ca152` made the private API ready in 29.479 s. Native start exited successfully at 01:06:24.564 UTC. At 01:06:25.096 Mango deferred inference because the provider operation lock was held. Luna finished both requests, including `0b768b46-3ef4-4962-a6bf-5017750c68f1`. The fallback queued for 18.471 s. The native runtime was healthy and belonged to the restarted process.

## Stable response model names and v0.0.22

During the two-worker test, Luna's native runtime supplied a per-load UUID in the streamed top-level model field. The second candidate, from commit `56bedbda7c87e3529a68c342f80ec58d544e6901`, maps that runtime value to the client's requested model or alias. It also filters supplemental runtime UUID entries from model listings while preserving the stable model and its configured alias. Regression coverage splits SSE at every byte boundary and checks nested fields and user content. Simultaneous admission is covered through one ingress and through two independent ingresses.

| Platform | Signed ZIP bytes | SHA-256 |
| --- | ---: | --- |
| Windows x64 | 64,763,521 | `6ade5bcb81a9494f1ff42b27dba0876411a535ef51dccb977fcb232a70be9080` |
| Linux x64 | 50,698,787 | `1517a6df4dedd3a30e7d73fab54e0a476b704ac6f658bf9df311e37e7a8d3a67` |
| Linux arm64 | 47,893,042 | `941fba313ad31ef06519d11e277127b836be2b5e759862ba784461b676a76499` |

The pinned SDK Release build and full host suite passed. The signed Windows v0.0.20 and v0.0.22 binaries forwarded in both directions with one attempt each, requests `80c1c79d-e37d-4886-bc02-c12381da2e52` and `1810791b-24e8-4e4e-a29a-52368bfccaf0`. On star-server-mini both Linux x64 smoke scripts exited zero on the verified v0.0.22 bytes, including a managed upgrade and fallback.

The v0.0.22 Windows package qualification ran for 901.7 s with 0.2565% of a core, 133.0 MiB peak resident memory and zero idle peer requests. Warm HTTP p95 was 2.480 ms, including 1.805 ms outside the provider. The v0.0.21 and v0.0.22 assets were retained as private draft candidates. The public release candidate below also addresses the cold-start race.

## Cold-start admission fix and v0.0.23

Commit `f1284eef68105f9cfe2a2fb5316d7d040a7b1336` keeps the entry's place until its provider preparation completes. A subsequent catalog operation holding the provider lock also allows the just-prepared request to retry admission locally. Tests cover early publication of the resident instance, transient lock contention after preparation and simultaneous arrivals across an actual listener stop and restart. The native-start race fixture failed on the prior implementation and passed after the change.

| Platform | Signed ZIP bytes | SHA-256 |
| --- | ---: | --- |
| Windows x64 | 64,763,685 | `bbc29cdc059cb55e7928d9bd139942d65833d83a398c6443ea9f55ed7c429eed` |
| Linux x64 | 50,699,004 | `ebb7f3851f6a546b2db6068bd409d00b58b0d3fa217eee5a4af7c425d5194d75` |
| Linux arm64 | 47,893,252 | `2a328f31795f4100dcedebf7ef43fbc1867c9437cf010b7b58e0ffc1cc7391b3` |

The final signed Windows binary ran 901.7 s with 20 peers and 100 deployments at 0.2547% of one core, 132.7 MiB peak resident memory and zero idle peer HTTP calls. Across 100 warmed requests HTTP p95 was 2.013 ms and p95 outside the provider was 1.651 ms. The shared edit sent 19 deltas and two missing-model requests shared one discovery round. Both Linux smoke scripts passed on the signature-verified v0.0.23 Linux x64 package on star-server-mini. All 22 Web UI tests passed. Signed v0.0.20 and v0.0.23 forwarded in both directions with one execution per request, IDs `5a476fc1-6021-4ffe-b961-11dbde1b287a` and `bbb2e26c-01e7-4981-ab1e-4156825d9cfa`.

## Real alternate preserves local residency

Two isolated Mango nodes on Nuc-eGPU-box used real LM Studio and an independent CPU-only llama.cpp serving the installed Nemotron 4B GGUF. In the checked-to-YOLO direction, request `6d1bd490-13de-450c-971b-f307750f6564`, attempt `c4bc4b41-3e8d-42c2-b74d-286a9e2fcf7`, reached first output on the warm alternate in 1.028 s. The reverse request `ae38f208-9f4b-484a-ab02-0aac99d2e3d7`, attempt `0c0db105-2f56-441a-a6fd-1a1a0c9ca4a8`, reached first output in 0.257 s. Both streamed through EOF with HTTP 200. Local Gemma stayed cold and 50 native inventory samples showed the original local Nemotron and embedding instance IDs. After cleanup, the production embedding was the sole resident at 512 context and returned 768 dimensions.


## External activity and cooldown

On GamingDesktop, sustained external CPU activity triggered whole-node withdrawal. While yielding, the local Mango ingress forwarded a short chat to Luna. Continued activity cleared the quiet timer, which restarted after the test load subsided. The exclusive Strata adapter enforces checked mode. Nuc-eGPU-box provided a separate real managed LM Studio YOLO test.

At 01:18:07.987 UTC the isolated eGPU node withdrew after 10 seconds above its temporary CPU threshold. The authenticated ingress returned HTTP 200 during withdrawal and LM Studio reported the entire two-model YOLO group released at 01:18:10.688. Continuing external activity renewed the quiet deadline by 10 seconds. Contribution resumed at 01:18:36.042 at 0.86% CPU, with eight model IDs again listed. The host restored the original sole 512-context embedding and checked the 768-dimensional result directly and through Mango. The original production thresholds, cooldown and linked model settings were retained.


The unforced benchmark check on Nuc-eGPU-box used a fresh private profile for an actual CPU llama.cpp Nemotron deployment. After 5m 0.7s of foreground idle time, the scheduler started a cold benchmark. An arriving foreground request cleared benchmark activity in 188 ms before any pass was saved. Request `6230fb46-5cd8-45ba-9d97-e8d904b75b08`, attempt `cc065bda-e2f0-4db7-84b2-fa9500cdbd35`, reused the managed runtime, generated output in 4.252 s and completed HTTP 200 through EOF. The owned process exited when the private node stopped.

On GamingDesktop itself, a private v0.0.23 instance used a real Gemma 3 4B GGUF with CPU-only llama.cpp on two threads. The unforced warm benchmark started at 02:08:38.872 UTC following 301.49 seconds of idle time. A foreground request arrived while the native slot was busy. It reached first token in 1.639 s and finished HTTP 200 through EOF in 1.792 s. The benchmark settled without saving an incomplete speed result. Foreground request `3b8212d2-2dfa-44bc-b432-aeac26372ccb` had one attempt, `e9bb6482-b688-4ebf-b4e9-fcdd1b382377`. The private processes and listeners exited, and the production Flash Next runtime stayed online with its original settings.

## Final Windows canary

GamingDesktop reached idle at 01:26:43 UTC and reported signed v0.0.23 under its original identity at 01:26:52 UTC. Nine decrypt-tested configuration backups, its original program directory and signed previous package were retained. The first forwarded request, `79a7cec6-2e05-44cd-b2e4-9070c8f1334e`, completed on GamingDesktop with HTTP 200, attempt `bd996269-652b-4475-a9b0-9cac268c2fc0`. Native startup took 31.064 s and total model loading took 32.220 s. The warm synthetic request `f53cc4ee-f057-4772-8a33-943e87148b57`, attempt `35446039-a212-4499-b60d-942aae969d73`, completed through EOF after 8,070 uncached input tokens and 128 output tokens at 93.6 tokens/s. The additional Mango execution reserve was zero.


## Repeated GamingDesktop interruption with the corrected server

The Linux driver used two independent, preconnected, authenticated tailnet clients targeting Luna directly. Luna was still on v0.0.21 and GamingDesktop was on v0.0.23. The Gaming listener stopped at 01:38:33 UTC. An initial driver with a 30-second response-header timeout cancelled its queued client. The repeated run used a 330-second client timeout. No provider or queue settings changed.

| Availability at submission | Client send skew | Luna requests and completed attempts | Outcome |
| --- | ---: | --- | --- |
| Gaming listener down | 0.370 ms | `18ca87f8-a837-47d2-9628-638ca3c7742c` / `3d4e88b2-937a-44ba-b997-d6eae6761f8c` and `f1c781a5-a3bb-4048-ba12-8b8c23f7939f` / `4e5fe26a-96ca-4974-b437-7181ee53af8f` | Both completed 6,144 output tokens on Luna through HTTP 200, DONE and EOF. Waits were 0.062 ms and 71.830 s. Generation overlap was zero. |
| First successful Gaming peer identity response, 01:45:32.239 UTC | 0.223 ms | `ba286c98-96d4-40b9-a3bd-d0cc17126832` / `cf09f458-eeb1-4727-9e6a-4c7fd2bd3967` and `e6f53696-6b46-4be8-9a0f-6d393e42f183` / `f55fe432-9418-462c-930a-3c71e36d76e8` | Both completed on Luna through EOF. They took 62.106 and 130.780 s with zero generation overlap. Luna’s journal recorded one local attempt for each. |

The peer introduction endpoint is available during startup. Inference eligibility also requires reconciliation of network authority and a supported local placement. GamingDesktop subsequently completed a separate forwarded request after a 34.100 s cold load. The test left already admitted work on Luna.


Once GamingDesktop had demonstrably resumed serving, the next fresh direct-to-Luna barrier pair split without a manual refresh. Client sends were 0.379 ms apart. GamingDesktop served request `4e949e89-5e44-41cb-a0eb-a4f7ca5fff48`, attempt `92b3a7a7-e97f-44b6-842c-7aeb36b9d08e`, with 0.0576 ms in queue. Luna served `c97c11f4-2854-4b4a-9ba5-286aec9ca2df`, attempt `b905a1fd-fc79-40e0-83df-ee9408536942`, with 0.0598 ms in queue. Both completed HTTP 200, DONE and EOF with 6,144 output tokens each. Client-observed generation overlapped from 01:49:25.830187 to 01:50:20.389082 UTC, or 54.559 s.

## Published release and fleet verification

The [public v0.0.23 release](https://github.com/kristopher-miles/Mango/releases/tag/v0.0.23) went live at 01:51:15 UTC on November 22. All twelve GitHub asset digests and sizes matched local release files before publication. The public tag resolved to `f1284eef68105f9cfe2a2fb5316d7d040a7b1336`. The signed peer update returned queued for Nuc-eGPU-box, Luna and star-server-mini and already current for GamingDesktop.

NucBox, whose owner had disabled daily updates, used the packaged side-by-side launch procedure. It kept daily updates and deferral off, retained its leech role, original network and node identity and four persisted inference-key identities. The local settings, providers and public ingress configuration files were byte-identical before and after. The previous program and encrypted backups remain on this host.


The final listener inventory verified `0.0.23/1` and the enrolled node ID on every address.

| Node | Enrolled ID | Final role | Signed package |
| --- | --- | --- | --- |
| GamingDesktop | `8189c76d-07e4-4ee0-bdc1-514b24a6890a` | Contributor | Windows x64 |
| luna-station | `48810fbe-1a0b-4388-80c3-10ccfa36606a` | Contributor | Windows x64 |
| Nuc-eGPU-box | `1846280d-5bb8-4507-9a01-9b615b57801c` | Contributor | Windows x64 |
| NucBox_M6Ultra | `d732ffb6-14fd-43da-80c4-cd0636614bb2` | Leech | Windows x64 |
| star-server-mini | `8df9f4b3-3112-4254-8f8b-fed3d7e4eccb` | Leech | Linux x64 |

The managed update on Nuc-eGPU-box preserved startup, benchmark preferences and seven disabled LM Link models. All 462 installed runtime files matched the signed ZIP. Both native and Mango embedding requests returned 768 dimensions with the original model the sole resident at 512 context. On star-server-mini, the signed-feed update preserved the systemd launcher, service identity, enrollment, role, daily updates, inference-key records and private Docker relay configuration. The previous program and protected recovery snapshot were retained. Authenticated relay model listing and a `text-embedding-nomic-embed-text-v2-moe` request returned the expected model and 768 dimensions.

At 01:54:30 UTC, an additional NucBox pair encountered a third production request. That independent request occupied GamingDesktop's one slot when it processed the second client's attempt. GamingDesktop deferred before native inference and both test requests finished on Luna. The separate production request was serving attempt `ab7e3f9f-01d7-4b00-8e63-4ee47542cd0a`. Both Luna streams returned the stable `flash-next` name.


With the third request finished, the repeated NucBox pair arrived 0.9 µs apart. Each request made exactly one attempt. Luna served `ce66554c-833e-4d5a-aae1-165fe4b1a21b` / `885938af-fcd8-4bf3-a845-11a8e235d0c9`. GamingDesktop served `987c120c-2a9a-4e77-ab56-68de204522ef` / `810ae5ee-a474-4368-9797-aea98a13de6d`. Generation overlapped from 01:56:56.370 to 01:57:32.474 UTC (36.104 s). Both returned HTTP 200 through EOF with 6,144 output tokens. The two streamed model values were `flash-next` and `qwen3.8-flash-next-iq2_xs`. NucBox's authenticated model list included the alias and stable worker model names, and contained no supplemental runtime UUID.


### Simultaneous and staggered requests on the published release

Both Windows workers and the Linux ingress ran v0.0.23. The authenticated driver drained every stream through the final marker and EOF. Each of the six requests below had one completed native attempt and produced 6,144 output tokens. The streamed model names were the configured alias `flash-next` and GamingDesktop's stable native `qwen3.8-flash-next-iq2_xs`.

| Ingress and timing | Luna request / attempt | GamingDesktop request / attempt | Generation overlap |
| --- | --- | --- | ---: |
| Direct Luna, simultaneous | `d0b58826-820f-4694-aba4-1bbbffaf99ad` / `bfc8298e-e62b-40d4-8438-766443c45a8d` | `e640785d-a0b4-4335-b0f6-f90f9bcea3ae` / `ce1caaa7-334a-4936-8bcb-95c2bd8d324d` | 58.328 s |
| Applebot relay to star-server-mini, simultaneous | `4aa4ba7e-ecde-4455-acee-d54426439ec1` / `0a5c587e-5b59-4722-891c-303d50882208` | `82d39a9e-61b2-44bf-a032-69fe8aa2a2fe` / `2e6a7063-5a3a-45fe-9695-c06602f19fc6` | 54.113 s |
| Applebot relay to star-server-mini, staggered | `b25608aa-7f4f-48c6-bc60-1957f5479dc5` / `ce4bdf26-a347-470d-81bb-8d5064ac711e` | `6f5f043c-c28e-4908-9e26-0c639bec7f3f` / `c8335a51-752e-4326-bd74-620e576579b0` | 45.706 s |


On direct Luna, the sends were 0.211 ms apart and generation overlapped from 01:59:59.662839 to 02:00:57.990720 UTC. On the Linux relay the simultaneous sends were 0.279 ms apart, with overlap from 02:01:19.740740 to 02:02:13.853949 UTC. In the separate staggered Linux run, B sent 0.142 ms after A's first generated output. The two streams overlapped from 02:02:48.790916 to 02:03:34.496666 UTC.

### Applebot primary application path

The real application `Service.reply` ran with its production-selected provider and private relay under an isolated synthetic application scope. The embedding, metadata and reply stages each returned HTTP 200. The reply resolved locally and was confirmed to a private sink. Application reply `20261122T020448.360507Z_c242c004f4194f9c9418642b7ff40fc1` matched Mango request `8b26a804-2183-4227-b32e-55f16d09ac6d` and attempt `feca9862-7012-4a8f-bf1a-cf30ea4c676b`. The relay configuration remained byte-identical. The test removed its temporary clients and runtime state. No naturally occurring post-upgrade application job had completed at the time this correlation was recorded.
