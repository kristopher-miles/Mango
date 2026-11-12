# Phase 7 Logging and retention

Deliver optional local request/operational logs, useful partial daily network logs, and network-wide retention/deletion. Depends on phases 3–6. Consume the existing request timings and state transitions; do not add a new telemetry transport or event framework.

Read the shared foundation and LOG-01–18 in [Operations requirements](../requirements/04-interface-and-operations.md).

## Capture and storage

Implement four request modes under shared configuration: Off, Sparse, Verbose, and Everything. Default to Sparse. Off has no durable request history; Sparse stores metadata; Verbose adds exact request bodies with known credential exclusions; Everything adds response bodies, including streamed output. Keep key IDs and non-secret names, never credential tokens, cookies, master/recovery material, or provider secrets. Model content may itself contain private information; do not claim arbitrary secret detection.

Record original request and attempt IDs, source IP and its provenance, key/node identity and name-at-time, ingress/destination, alias and actual deployment, task, priority, outcome/reason, reroutes, context/request length, load and queue delay, first-token timing, elapsed time, and tokens/second. Use relevant duration/size units for media. Keep missing/inapplicable metrics distinct from zero. Use local monotonic durations and UTC event timestamps; do not derive latency by subtracting unsynchronized peer clocks.

Capture payloads once at ingress and execution metadata at the serving node. Correlate them through existing IDs instead of copying full requests at every hop. Tag benchmark traffic separately from client traffic. For streamed output, tee bounded chunks to the log writer while forwarding; logging must not turn streaming into response-wide buffering.

Use daily append files with structured metadata and separate payload files where binary content makes that simpler. One bounded background writer handles disk I/O. If storage or the writer falls behind, mark the capture incomplete and count dropped data; never block inference indefinitely or label a truncated body exact. Recover a partial final log record after a crash without losing earlier valid records.

Operational logging is a separate disableable setting. Record resource-pressure withdrawal, managed load/unload failures, rejoin/merge outcomes and relevant faults with compact diagnostics. It does not become a hidden request-content log. Required credential/deletion/configuration state still persists when all logs are off.

## Daily and on-demand collection

At the shared timezone's end of day, candidates contact lower-join-ID known peers as part of that actual collection job. The lowest reachable enrolled node collects; a leech is eligible. Use bounded connection attempts and a compact running-job marker to avoid obvious duplicate work. No continuously elected leader, recurring leader lease, or online-map polling is needed. Partitions may create multiple partial collectors; the merge must tolerate that.

The collector requests records by node ID and sequence/watermark in bounded chunks. Deduplicate event identity and distinguish original client requests from repeated attempts. Report each peer as received, unavailable, logging off, errored, or beyond retention. Missing data is not zero usage. Return the current partial aggregate immediately when requested and mark its coverage; late records improve it without duplicating what was already collected.

Store UTC timestamps and interpret daily boundaries using one configured IANA timezone defaulted during network creation. Use the platform timezone handling for daylight-saving boundaries; do not assume every day is 24 hours. Manual collection uses the same job path and may request a day or current partial day. Throttle background file transfer so it does not consume inference capacity or interfere with local resource protection.

## Retention and clear

Default to seven days of raw records, thirty days of aggregate metadata, and a 1 GiB total log cap per node. Apply the raw window to Verbose/Everything payloads. Remove oldest eligible material first, and report when the byte cap makes actual retention shorter. Count aggregate copies and payload files toward the cap; do not retain hidden archives.

Master-authorized network clear advances the existing deletion generation, removes local log/payload/aggregate data, and pushes that state. Track pending unreachable peers compactly. On reconnect, apply deletion before uploading older logs. A complete-clear claim requires the relevant peers to acknowledge or be removed; incomplete propagation does not prevent clearing reachable nodes. User exports remain outside Mango's control.

Full reset uses this same deletion path and clears sensitive temporary request/affinity state. Ordinary password recovery does not. Keep deletion-generation records even with logging disabled so an old peer cannot restore cleared history.

## GUI and output

Add mode, operational-log enablement, retention, timezone, collection and clear controls to the existing UI. Show usage summaries by key name and client IP, plus request timing/destination detail; a simple table and filters are enough. Provide a downloadable aggregate containing metadata and the retained payloads appropriate to its modes. It must identify incomplete coverage. No analytics database, QoS enforcement engine, dashboard service, or indefinite archive replication.

## Minimum checks

- Run one streamed text request and one binary/media request through all four modes. Confirm exactness where complete, secret-field exclusion, key-name/IP attribution, and no request payloads when Off/Sparse.
- Collect twice with an offline peer and a retried request; totals deduplicate and a useful partial result remains available. Test a late merge and a daylight-saving day with small fixtures.
- Fill a small test quota, clear while a peer is offline, and reconnect it. Old payloads disappear and do not return. Verify operational logging can be disabled independently.
- Measure that a slow/failing log writer does not block token forwarding indefinitely and that incomplete capture is visible.

## Done

The owner can inspect or export useful network logs with known coverage, choose how much to retain, and clear them across intermittent peers. Logging consumes bounded storage and memory and remains optional. No new inference-time peer calls are introduced by logging.
