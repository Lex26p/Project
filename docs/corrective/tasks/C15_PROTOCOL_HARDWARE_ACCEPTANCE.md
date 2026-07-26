# C15 — Read-only приёмка реальных Modbus TCP и SNMP устройств

## 1. Назначение

Подтвердить работу production adapters C12/C13 на предоставленных пользователем устройствах без физической записи.

## 2. Внешняя зависимость

Для выполнения пользователь предоставляет через локальную защищённую конфигурацию:

- адреса и ports;
- Modbus unit ID и разрешённые read registers;
- SNMP OIDs и secret reference;
- модели/версии устройств;
- допустимую частоту лабораторного опроса.

Значения не записываются в repository или итоговый отчёт, если они чувствительны.

## 3. Архитектурный контекст

Прочитать:

- `CORRECTIVE_ARCHITECTURE_SPECIFICATION.md`, разделы 14, 16 и 19;
- `CORRECTIVE_ROADMAP.md`, Gate R3;
- `TASK_EXECUTION_RULES.md`.

## 4. Отправные файлы

- production Modbus/SNMP composition C12/C13
- engineering diagnostics C14
- `src/Dispatcher.Modbus/*`
- `src/Dispatcher.Snmp/*`
- `src/Dispatcher.Protocols/*`
- `src/Dispatcher.RuntimeHost/*`
- protocol unit/integration tests

Остальные файлы изучать по необходимости.

## 5. Целевой результат

Для каждого устройства получено sanitised evidence:

- successful connection and sample poll;
- корректное значение/type/scale/unit/byte order или OID mapping;
- current/history прохождение;
- quality/freshness при нормальной работе;
- timeout и недоступность;
- disconnect/reconnect;
- restart RuntimeHost;
- bounded частота/параллелизм;
- отсутствие write PDU/function.

## 6. Объём реализации

- Подготовить reusable local lab profile/runbook без credentials.
- При необходимости добавить безопасный probe mode через существующий diagnostic job, не отдельный обход runtime.
- Выполнить baseline, disconnect и recovery scenarios.
- Зафиксировать измеренные latency/error/retry counters.
- Проверить, что logs и reports не содержат community/password.
- Создать `docs/operations/PROTOCOL_LAB_ACCEPTANCE.md` с sanitised результатами и известными ограничениями.

## 7. Безопасность

- Не отправлять Modbus write function.
- Не отправлять SNMP SET.
- Не увеличивать load выше согласованной пользователем границы.
- Не менять конфигурацию устройства.
- Не сохранять packet capture с plaintext community в проекте.
- При неоднозначной карте registers/OIDs остановить соответствующий сценарий и запросить уточнение.

## 8. Критерии приёмки

- Хотя бы одно Modbus TCP и одно SNMP устройство прошли read-only happy path.
- Disconnect и recovery видны в runtime quality/readiness.
- После recovery polling продолжается с новой session fence.
- Wire-level либо эквивалентное evidence подтверждает только FC03/FC04 и SNMP GET.
- Результаты воспроизводимы по runbook.

## 9. За пределами задания

- Сертификация всех моделей оборудования.
- Performance stress устройства.
- Firmware update.
- Physical commands.

## 10. Итоговый отчёт

Указать sanitised модели, сценарии, измерения, найденные mapping issues и подтверждение read-only boundary.

