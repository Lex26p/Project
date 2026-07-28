(() => {
    const storageKey = "dispatcher.ui.preferences";
    const defaultPreferences = {
        theme: "light",
        density: "comfortable"
    };

    function normalize(value) {
        return {
            theme: value?.theme === "dark"
                ? "dark"
                : "light",
            density: value?.density === "compact"
                ? "compact"
                : "comfortable"
        };
    }

    function readPreferences() {
        try {
            const stored = window.localStorage.getItem(storageKey);
            return normalize(stored ? JSON.parse(stored) : null);
        } catch {
            return { ...defaultPreferences };
        }
    }

    function applyPreferences(theme, density) {
        const value = normalize({ theme, density });
        document.documentElement.dataset.theme = value.theme;
        document.documentElement.dataset.density = value.density;
        return value;
    }

    function writePreferences(theme, density) {
        const value = applyPreferences(theme, density);
        try {
            window.localStorage.setItem(
                storageKey,
                JSON.stringify(value));
        } catch {
            // Presentation preferences remain active for this tab.
        }
    }

    function focusElement(id) {
        const element =
            document.getElementById(id);
        if (!element) {
            return false;
        }

        element.focus();
        return document.activeElement === element;
    }

    window.dispatcherUi = {
        readPreferences,
        writePreferences,
        focusElement
    };

    const initial = readPreferences();
    applyPreferences(initial.theme, initial.density);
})();
