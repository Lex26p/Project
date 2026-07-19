# ADR-007 — ограниченный runtime current и delta retention

**Статус:** Accepted  
**Дата:** 19 июля 2026 года  
**Область:** `S14`, bounded runtime state и slow-consumer recovery

## Контекст

Расширенный `S14` workload/fault corpus обнаружил, что `CoreRuntime` сохранял все current transitions в памяти без ограничения. Это нарушало обязательство bounded resource use и позволяло медленному либо отсутствующему consumer неограниченно увеличивать retained delta state. Число point в current также не имело явной capacity boundary.

При этом экспериментальные значения workload нельзя превращать в нормативные production limits без отдельного operations evidence.

## Решение

1. `CoreRuntime` принимает явно заданные вызывающей стороной `RuntimeCurrentLimits`: максимальное число current point и capacity retained current transitions.
2. RuntimeCut, добавляющий point сверх capacity, отклоняется целиком до изменения source position, current, liveness или clocks.
3. Current transitions сохраняются в ограниченной FIFO-очереди. Удаление старого transition не изменяет authoritative current snapshot.
4. Consumer cursor старше earliest retained transition считается gap и требует нового authorized snapshot. Cursor впереди current также отклоняется.
5. После checkpoint restore delta history не выдумывается: cursor до восстановленной current position требует resnapshot.
6. Конкретные capacity передаются deployment/test composition и не объявляются production-нормативом результатом `S14`.

## Рассмотренные альтернативы

- Неограниченный список transitions отклонён из-за доказанного unbounded growth.
- Неявный hard-coded global limit отклонён как произвольный production contract без workload/operations evidence.
- Silent truncation с продолжением старого cursor отклонена, поскольку скрывала бы realtime gap.
- Сохранение delta history в checkpoint отклонено: current checkpoint является rebuildable snapshot, а realtime не является History.

## Последствия

- Каждая runtime composition обязана выбрать явные resource limits для своего evidence profile.
- Slow consumer изолирован bounded retention и восстанавливается через существующий snapshot/gap contract.
- Point-capacity exhaustion является видимым atomic failure без частичного position advance.
- Golden/property/fault/Windows workload tests проверяют determinism, restart, clock regression, mass timeout, slow consumer и bounded state.
- Linux semantic/load parity не заявляется по решению пользователя; `IG-01` остаётся Partial.
