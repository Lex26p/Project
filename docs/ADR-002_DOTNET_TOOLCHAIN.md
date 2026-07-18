# ADR-002 — .NET toolchain и repository foundation

**Статус:** Accepted  
**Дата:** 19 июля 2026 года  
**Область:** `IG-01`, toolchain/build/test foundation

## Контекст

`S01` требует единый C#/.NET toolchain, минимальную solution structure, обязательные build policies и точки входа unit/integration tests. Рабочая среда — Visual Studio 2026 и .NET 10.

## Решение

1. Repository использует .NET 10 и `net10.0`; `global.json` ограничивает выбор SDK линией 10.0.
2. Solution хранится в формате `.slnx` и пока содержит только явно требуемые test entry points. Предметные и будущие модули не создаются.
3. Общие build properties включают nullable analysis, .NET analyzers, code-style enforcement, warnings as errors и deterministic build.
4. Версии test packages управляются централизованно; restore фиксируется lock-файлами.
5. CI выполняет restore, Release build и tests на Windows x64.
6. Linux validation не выполняется по прямому указанию пользователя от 19 июля 2026 года. До отдельного Linux evidence соответствующая часть `IG-01` остаётся незакрытой.

## Последствия

- Один managed toolchain обслуживает foundation и последующие C# projects.
- Windows x64 является проверяемой средой текущего спринта.
- В repository отсутствуют native toolchain и Windows-only production dependencies.
- Переход к следующему спринту не заявляется до обновления gate status по фактическому evidence.
