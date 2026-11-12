// Sayfa çizilmeden önce kayıtlı temayı uygular (açık/koyu geçişinde "yanıp sönme" olmaması için <head> içinde yüklenir).
(function () {
    try {
        var saved = localStorage.getItem('ap-theme');
        var theme = saved || (window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light');
        document.documentElement.setAttribute('data-bs-theme', theme);
    } catch (e) { /* localStorage kapalıysa varsayılan açık tema kalır */ }
})();
