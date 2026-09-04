# LANG-43

## Fixes in Main.cs

1. **Do not clear `LastLanguage`** when switching to official language.
2. **`ForceCustomLanguage`**: only skip if ActiveLanguage is official **and not** English (`ReferenceLanguage`). English boot still restores last custom pack.
3. **WaitForGameUiThenReload**: same English exception for ForceCustom.
4. Remove `OptionsSelector.Set` DEBUG spam.

## Expected boot log

```
SET ACTIVE LANGUAGE: English
ForceCustomLanguage begin — LastLanguage=Tiếng Việt
SET ACTIVE LANGUAGE: Tiếng Việt
```

Not:

```
Bỏ ForceCustomLanguage — ActiveLanguage official = English
```
