# MyASR

Приложение для локальной диктовки в Windows. Выберите микрофон и модель, нажмите «Начать запись» и диктуйте. Текст появляется в поле «Расшифровка» во время записи. По мере продолжения фразы модель уточняет последние слова. Когда закончите, нажмите «Остановить запись» — приложение обработает окончание фразы и скопирует итоговую диктовку в буфер обмена. Глобальное сочетание клавиш (по умолчанию `Ctrl+Shift+X`) также переключает запись. В главном окне WPF можно скопировать расшифровку или сохранить её в TXT.

Для всех окон используется сохранённая тема. Параметр `DarkTheme` в файле `%LOCALAPPDATA%\MyASR\settings.json` задаёт тёмную (`true`) или светлую (`false`) тему при запуске.

Для запуска из исходников нужны Windows x64 и .NET 11 SDK.

```powershell
dotnet run --project MyASR
```

[Замеры качества распознавания на русском и команды для повторной проверки](docs/asr-quality-ru.md)

## CrisperWhisper 2.0 model export

`nyralabs/CrisperWhisper2.0_small` has no published ONNX build, so MyASR exports it:

```bash
python -m pip install -r tools/requirements-crisperwhisper.txt
python tools/export_crisperwhisper_onnx.py --out build/crisperwhisper
```

The script prints a `ModelCatalog` file list for each precision. Publish the folders to an
HTTP source, or point MyASR at them locally:

```
setx MYASR_MODEL_SOURCE_CRISPERWHISPER2_SMALL_INT8 C:\path\to\build\crisperwhisper\int8
```

The model is licensed `nyra-health-non-commercial-research` — non-commercial research use only.
