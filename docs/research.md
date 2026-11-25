# Исследование перед реализацией

Изучены три снимка исходного кода на 25 ноября 2026 года. Для SSD Guardian написан собственный код. TraceEvent подключён как пакет Microsoft.

## DiskActivityMonitor

**Ревизия** `1429d059fafc5c47dadc2f2851b98f8bbf2dabc1`. В дереве ревизии отсутствует файл лицензии и GitHub API не указывает SPDX-идентификатор. [Дерево исходников](https://github.com/andrewtheart/DiskActivityMonitor/tree/1429d059fafc5c47dadc2f2851b98f8bbf2dabc1)

- Служба читает `FileIO`/`FileIOInit`, суммирует запросы по имени процесса и по нормализованному пути. UNC проходит фильтр файловых целей. При отказе ETW служба использует общие счётчики I/O процесса. [ETW-сборщик](https://github.com/andrewtheart/DiskActivityMonitor/blob/1429d059fafc5c47dadc2f2851b98f8bbf2dabc1/src/DiskActivityMonitor.Service/EtwProcessIoReader.cs)
- Физическая активность берётся из `PhysicalDisk` и связывается с `MSFT_PhysicalDisk`. Пользовательские алерты используют пороги по логическим объёмам и отдельно приводят физическую активность. [Источники данных](https://github.com/andrewtheart/DiskActivityMonitor/blob/1429d059fafc5c47dadc2f2851b98f8bbf2dabc1/docs/DATA-SOURCES.md) · [Правила алертов](https://github.com/andrewtheart/DiskActivityMonitor/blob/1429d059fafc5c47dadc2f2851b98f8bbf2dabc1/src/DiskActivityMonitor.Core/Alerts/AlertEngine.cs)
- Периодическая выгрузка минутных агрегатов в базу и политика хранения обслуживают историю. У приложения есть отдельные процессы службы и трея. [Цикл сбора](https://github.com/andrewtheart/DiskActivityMonitor/blob/1429d059fafc5c47dadc2f2851b98f8bbf2dabc1/src/DiskActivityMonitor.Service/CollectorWorker.cs)

## DiskGuard

**Ревизия** `71967992fd75ec70e2b6c1d0e6bef90358e21ed7`. Лицензия MIT. [Лицензия](https://github.com/XiaoRui114514/DiskGuard/blob/71967992fd75ec70e2b6c1d0e6bef90358e21ed7/LICENSE)

- Основной сборщик использует `Microsoft-Windows-Kernel-Disk`, резервный использует классический `DiskIO`, а при отказе обоих доступны общие счётчики процесса. Байты дисковых событий группируются по PID и отфильтрованному номеру диска. Метаданные файлов в этом сборщике отсутствуют. [ETW-сборщик](https://github.com/XiaoRui114514/DiskGuard/blob/71967992fd75ec70e2b6c1d0e6bef90358e21ed7/src/DiskGuard.Core/Monitoring/EtwProcessIoSource.cs) · [Выбор источника](https://github.com/XiaoRui114514/DiskGuard/blob/71967992fd75ec70e2b6c1d0e6bef90358e21ed7/src/DiskGuard.Core/Monitoring/ProcessIoSourceFactory.cs)
- PDH `PhysicalDisk(*)` даёт загрузку, очередь и скорость. Движок анализирует среднюю загрузку в окне и может ограничивать процессы. Накладные расходы источника зависят от частоты событий и опросов. Сглаживание ETW использует до трёх последних отсчётов. [PDH-сборщик](https://github.com/XiaoRui114514/DiskGuard/blob/71967992fd75ec70e2b6c1d0e6bef90358e21ed7/src/DiskGuard.Core/Monitoring/DiskSampler.cs) · [Движок](https://github.com/XiaoRui114514/DiskGuard/blob/71967992fd75ec70e2b6c1d0e6bef90358e21ed7/src/DiskGuard.Core/Engine/GuardEngine.cs)

## ssd-health-monitor

**Ревизия** `b22e6a348b0e469bf8a2403a0a97c84713c66814`. Лицензия MIT. [Лицензия](https://github.com/realsharpswing/ssd-health-monitor/blob/b22e6a348b0e469bf8a2403a0a97c84713c66814/LICENSE)

- PowerShell-скрипт сопоставляет модели по шаблонам, читает SMART через CrystalDiskInfo при наличии или использует `Get-PhysicalDisk` и `Get-StorageReliabilityCounter`. [Сборщик накопителей](https://github.com/realsharpswing/ssd-health-monitor/blob/b22e6a348b0e469bf8a2403a0a97c84713c66814/lib/collect-ssd.ps1)
- Кратковременный аудит опрашивает `Process(*)\IO Write Bytes/sec`, сравнивает значения с базовым уровнем и порогами, применяет список известных писателей. Глубокий режим читает `Win32_Process.WriteTransferCount`. Показатели включают общий ввод-вывод процесса, поэтому по ним нельзя связать байты с конкретным SSD или файлом. Базовый аудит по умолчанию занимает 30 секунд с шагом 2 секунды. [Аудит](https://github.com/realsharpswing/ssd-health-monitor/blob/b22e6a348b0e469bf8a2403a0a97c84713c66814/lib/audit-process.ps1) · [Конфигурация](https://github.com/realsharpswing/ssd-health-monitor/blob/b22e6a348b0e469bf8a2403a0a97c84713c66814/ssd-health-config.json)

## Решения для SSD Guardian

Окно WinForms, ETW-сборщик и правила работают в одном процессе. `DiskIO` считает физические операции по номеру диска и размеру передачи. `FileIO` определяет логические операции по пути и экземпляру процесса. Связь через один накопитель и временное окно служит признаком для эвристики. Измерение NAND-записи потребовало бы отдельного источника. [Поля DiskIO](https://learn.microsoft.com/windows/win32/etw/diskio-typegroup1) · [Поля FileIO](https://learn.microsoft.com/windows/win32/etw/fileio-readwrite)

Тома сопоставляются с номерами накопителей через документированные Windows API. Сетевые пути и неоднозначные соответствия исключаются из атрибутированных логических итогов. Для физической записи с нескольких дисков сохраняются отдельные счётчики. [Связь тома с накопителями](https://learn.microsoft.com/windows/win32/api/winioctl/ni-winioctl-ioctl_volume_get_volume_disk_extents)

ETW обрабатывается в реальном времени, агрегаты хранятся в ограниченных структурах в памяти. История и диагностика сохраняются только при изменениях. Фактические показатели SSD Guardian приведены в отдельном [отчёте о замерах](performance.md).
