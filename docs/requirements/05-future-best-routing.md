# Mango future best model routing

The `best` feature is explicitly deferred. Mango should record the information it will need, but should not yet run a decision model or choose unnamed models on the user's behalf. Serving a user-requested structured-decision endpoint is separate from using a decision model internally for routing.

## Future behavior

**BEST-01 — F.** A request for the reserved `best` model asks a configured decision model which capability is most relevant to the task, using the same user-defined labels that appear in model ratings. A programmatic selector then chooses an eligible deployment based on that capability and current availability.

**BEST-02 — F.** Before ranking, check authorization, actual provider capabilities, and availability. Use reported context as evidence when ranking. The provider validates the request. Apply the same additional-load and residency rules as ordinary routing.

**BEST-03 — F.** First consider eligible deployments with open execution capacity for the identified task. Choose the best capability fit among those, rather than waiting for an unavailable model solely because its rating is higher.

**BEST-04 — F.** If none has open capacity, prefer eligible deployments whose models are already loaded, choosing the strongest capability among that group and applying normal queue rules. If that group cannot legally admit the request, queue for the best otherwise eligible deployment under normal admission rules. A low-priority request that cannot legally queue is still rejected as overloaded.

**BEST-05 — F.** Remember a short client and request profile. Prefer the same endpoint for a related request for up to five minutes after completion, subject to current provider availability, authorization, and ordinary affinity and queue rules.

**BEST-06 — F.** Explain the selected capability, chosen alias/deployment, availability tier, and reason for a change in the GUI. Do not describe a user rating as an objective quality assessment.

## Future selection details

**BEST-07 — P, Q28.** “Open capacity” may include an unloaded deployment that can safely load and start without waiting behind another request. The loaded fallback group means loaded but currently occupied deployments that can legally accept waiting work. Otherwise the stated fallback repeats the same empty group and does not define an executable selection rule.

**BEST-08 — F, Q28.** Models may have independent 0–5 ratings for any number of arbitrary user-defined tasks, such as coding, creative writing, formal writing, and summarization. Classify each incoming request to exactly one best-matching keyword among the configured choices. Do not restrict a model to one capability. Selection then uses that keyword and the documented capacity tiers; no learned composite scoring service is required. This remains a future feature.

**BEST-09 — P, Q28.** Use an explicitly selected reachable decision deployment for classification. Bound the prompt, output schema, and latency. Treat its answer as data. Classification follows normal admission and never recursively invokes best-model routing.

**BEST-10 — P, Q28.** Prefer an explicit client task label when supplied and allowed. If classification is unavailable or invalid, use an owner-selected fallback capability/model or return a clear routing error; do not invent a confident capability. Sending a prompt to a classifier is additional disclosure within the private network and must follow scope and logging policy.

## Information required before the feature exists

**BEST-11 — I.** The core catalog and request records should distinguish task labels and ratings, technical features, effective context, loaded state, admission hints, measured latency, client/session identity, and resolved deployment. This is useful to ordinary routing already and avoids a future ambiguity over what “best” means.

**BEST-12 — P, Q28.** Reserve `best` as a special name and return a clear feature-not-enabled result until the feature is implemented. Do not allow an ordinary alias to collide with it, and do not silently route it to a default model. Record the requested logical model separately from the resolved deployment.

## Future acceptance examples

| Scenario | Expected future result |
| --- | --- |
| Strong coding model is busy; a weaker eligible coding model has open capacity | Use the open model, subject to ordinary priority admission. |
| Highest-rated model cannot fit the context | Exclude it before ranking. |
| No open capacity; one eligible loaded model can queue legally | Prefer that loaded model according to the capability ranking. |
| No loaded model can admit; an unloaded eligible deployment can queue legally | Queue for the best eligible destination under normal scheduling. |
| No legal queue for this low-priority request | Return overload; `best` does not bypass rejection policy. |
| Related follow-up arrives four minutes after completion | Prefer the previous endpoint if still eligible and useful. |
| Follow-up arrives after five minutes or clearly changes task | Reevaluate without assuming the prior capability. |
| Decision model is unavailable | Apply the approved fallback or return a clear error, without recursive `best` calls. |
