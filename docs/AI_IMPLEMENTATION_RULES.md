# Dispatcher — правила AI-разработчика

**Статус:** обязательные  
**Дата:** 18 июля 2026 года

## 1. Рабочий режим

1. Работай только над текущим sprint ID из `./DISPATCHER_SPRINT_CATALOG.md`.
2. Исходным состоянием считай указанный commit и фактический repository, а не память чата.
3. Сначала прочитай `./IMPLEMENTATION_STATE.md`, текущий sprint и только прямо относящиеся к нему разделы нормативных источников.
4. Не перечитывай весь комплект и не анализируй весь продукт заново, если текущий scope однозначен.
5. Локальные implementation decisions принимай самостоятельно и не запрашивай подтверждение без blocking причины.
6. Не публикуй длинное рассуждение. Сообщай результат, существенные решения, проверки и реальные blockers.

## 2. Неизменяемые границы

Без отдельного ADR и явного разрешения запрещено менять:

- product scope и maturity provisional-функций;
- Web/Kiosk/Wallboard → Server only;
- Server no southbound protocol I/O;
- Core/runtime authority и local Alarm authority;
- data ownership и запрет cross-owner writes;
- разделение realtime/History и owner positions;
- whole-revision publication/activation semantics;
- C#-first posture;
- process/service/native extraction gates;
- physical-command hard deny и safety gates.

## 3. Локальная автономия

Самостоятельно определяй:

- внутренние classes/methods и file organization;
- private module decomposition;
- algorithms, test fixtures и refactoring;
- названия non-public implementation types;
- размер технических подшагов внутри sprint;
- способ упаковки и передачи изменений пользователю.

Не создавай ADR для обычного локального решения, не меняющего внешние обязательства.

## 4. Запрещённые отклонения

- Не добавляй функции «заодно» или «на будущее».
- Не создавай empty future modules, microservices, broker, cache, IPC, C ABI или Driver SDK без текущего gate.
- Не создавай interface/repository/factory для каждого существительного.
- Не заменяй module ownership общей БД/универсальным event/status/workflow.
- Не фиксируй benchmark values как production limits без evidence decision.
- Не обходи security для demo; test identity должна быть environment-gated.
- Не оставляй незаявленные маркеры незавершённости, fake success, silent catch или production placeholder.
- Не выполняй physical writes вне user-authorized `S39–S40` qualification после `AR-08/DG-05`, applicable protocol-specific `DG-07`, scoped pre-production operations evidence и `IG-08`. Full `DG-08` и production enablement закрываются только в `S43` после final explicit decision.

## 5. Обязательный результат спринта

- Scope и non-goals соблюдены.
- Repository собирается; обязательные tests проходят.
- Добавлены tests уровня риска текущего изменения.
- Ранее принятые regression tests остаются green.
- Security, audit, observability и recovery учтены там, где применимо.
- Значимое решение отражено ADR; фактический статус — в `./IMPLEMENTATION_STATE.md`.
- Изменения ограничены текущим результатом; unrelated refactoring отсутствует.

## 6. Blocking decision

Останови только затронутую часть, если выполнение требует изменить глобальную границу, принять несовместимые источники либо сделать необратимый выбор без required evidence. Сформулируй кратко:

- противоречие;
- что блокируется;
- safe default;
- рекомендуемое решение и последствия.

Если безопасное локальное допущение не влияет на другие модули, прими его и зафиксируй без паузы.

Technical gates `IG-01–IG-07`, `IG-09`, `IG-10`, `IG-12` и `IG-13` закрывай самостоятельно через ADR и требуемое evidence, если применим safe default roadmap. `IG-08` physical-write authorization и `IG-11` product maturity требуют решения пользователя.

## 7. Краткая инструкция для нового implementation-чата

> Реализуй только указанный sprint Dispatcher на базе переданного commit. Следуй `docs/implementation/AI_IMPLEMENTATION_RULES.md`, `docs/implementation/IMPLEMENTATION_STATE.md` и sprint scope. Не перепроектируй приложение, не расширяй scope, не создавай abstractions или модули на будущее. Локальные решения принимай самостоятельно. Не описывай длинные рассуждения. Глобальное противоречие обозначь кратко и останови только затронутую часть. Результат должен собираться, проходить обязательные тесты и обновлять implementation state.
