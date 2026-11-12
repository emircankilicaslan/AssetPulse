// AssetPulse arayüz betiği. Sıkı içerik güvenlik politikası (inline betik yok) nedeniyle tüm davranışlar data-* ile bağlanır.
(function () {
    'use strict';

    var css = function (name) { return getComputedStyle(document.documentElement).getPropertyValue(name).trim(); };
    var isDark = function () { return document.documentElement.getAttribute('data-bs-theme') === 'dark'; };

    // ---- Tema anahtarı ----
    var toggle = document.getElementById('theme-toggle');
    if (toggle) {
        toggle.addEventListener('click', function () {
            var next = isDark() ? 'light' : 'dark';
            document.documentElement.setAttribute('data-bs-theme', next);
            try { localStorage.setItem('ap-theme', next); } catch (e) { /* yok say */ }
            window.dispatchEvent(new Event('ap-theme-changed'));
        });
    }

    // ---- Panelde otomatik yenileme ----
    var note = document.getElementById('refresh-note');
    if (note) {
        var seconds = parseInt(note.getAttribute('data-refresh-seconds'), 10) || 30;
        setTimeout(function () {
            // Açık bir pencere/form varken yenileme yapılmaz (yazılan not kaybolmasın).
            if (document.querySelector('.modal.show')) { return; }
            window.location.reload();
        }, seconds * 1000);
    }

    // ---- Varlık listesinde arama ----
    var search = document.getElementById('asset-search');
    if (search) {
        search.addEventListener('input', function () {
            var q = search.value.trim().toLowerCase();
            var shown = 0;
            document.querySelectorAll('.asset-item').forEach(function (el) {
                var match = !q || el.getAttribute('data-search').indexOf(q) !== -1;
                el.classList.toggle('d-none', !match);
                if (match) { shown++; }
            });
            var empty = document.getElementById('asset-empty');
            if (empty) { empty.classList.toggle('d-none', shown > 0); }
        });
    }

    // ---- Alarm üstlenme penceresi ----
    var ackModal = document.getElementById('ack-modal');
    if (ackModal) {
        ackModal.addEventListener('show.bs.modal', function (ev) {
            var b = ev.relatedTarget;
            if (!b) { return; }
            document.getElementById('ack-id').value = b.getAttribute('data-alert-id');
            document.getElementById('ack-return').value = b.getAttribute('data-return-url') || '';
            document.getElementById('ack-alert-title').textContent = b.getAttribute('data-alert-title') || '';
            document.getElementById('ack-note').value = '';
        });
    }

    // ---- Kopyala düğmesi ----
    document.querySelectorAll('[data-copy]').forEach(function (btn) {
        btn.addEventListener('click', function () {
            var input = document.querySelector(btn.getAttribute('data-copy'));
            if (!input) { return; }
            input.select();
            var done = function () { btn.textContent = 'Kopyalandı ✓'; };
            if (navigator.clipboard) { navigator.clipboard.writeText(input.value).then(done); }
            else { document.execCommand('copy'); done(); }
        });
    });

    if (typeof Chart === 'undefined') { return; }

    // ---- Sağlık dağılımı (halka grafik) ----
    var donut = document.getElementById('health-chart');
    if (donut) {
        var colors = [css('--ap-critical'), css('--ap-warning'), css('--ap-nodata'), css('--ap-normal')];
        new Chart(donut, {
            type: 'doughnut',
            data: {
                labels: donut.getAttribute('data-labels').split(','),
                datasets: [{ data: donut.getAttribute('data-values').split(',').map(Number), backgroundColor: colors, borderWidth: 0 }],
            },
            options: { animation: false, cutout: '68%', plugins: { legend: { position: 'bottom', labels: { color: css('--bs-secondary-color'), usePointStyle: true, pointStyleWidth: 9, boxHeight: 7 } } } },
        });
    }

    // ---- Sensör zaman serisi grafikleri ----
    var fmt = new Intl.DateTimeFormat('tr-TR', { timeZone: 'Europe/Istanbul', day: '2-digit', month: '2-digit', hour: '2-digit', minute: '2-digit' });
    var fmtTime = new Intl.DateTimeFormat('tr-TR', { timeZone: 'Europe/Istanbul', hour: '2-digit', minute: '2-digit' });
    var num = new Intl.NumberFormat('tr-TR', { maximumFractionDigits: 2 });

    document.querySelectorAll('.sensor-chart').forEach(function (canvas) {
        var card = canvas.closest('.sensor-card');
        var note = card.querySelector('.chart-note');
        var unit = canvas.getAttribute('data-unit');
        var thresholds = JSON.parse(canvas.getAttribute('data-thresholds') || '[]');
        var chart = null;
        var range = '24h';

        function render(data) {
            var raw = data.bucket === 'Raw';
            var pts = data.points;
            var labels = pts.map(function (p) { var d = new Date(p.t); return range === '24h' ? fmtTime.format(d) : fmt.format(d); });
            var text = css('--bs-secondary-color'), grid = isDark() ? 'rgba(255,255,255,.08)' : 'rgba(16,24,40,.07)';
            var brand = css('--ap-brand');
            var datasets = [];
            if (!raw) {
                datasets.push({ label: 'Maks', data: pts.map(function (p) { return p.max; }), borderColor: 'transparent', backgroundColor: brand + '22', pointRadius: 0, fill: '+1', tension: .25 });
                datasets.push({ label: 'Min', data: pts.map(function (p) { return p.min; }), borderColor: 'transparent', pointRadius: 0, fill: false, tension: .25 });
            }
            datasets.push({ label: raw ? 'Ölçüm' : 'Ortalama', data: pts.map(function (p) { return p.avg; }), borderColor: brand, backgroundColor: brand, borderWidth: 2, pointRadius: 0, pointHoverRadius: 4, tension: .25, fill: false });
            thresholds.forEach(function (t) {
                var color = t.severity === 'Critical' ? css('--ap-critical') : css('--ap-warning');
                datasets.push({ label: 'Eşik ' + (t.kind === 'UpperLimit' ? '>' : '<') + ' ' + num.format(t.value), data: pts.map(function () { return t.value; }), borderColor: color, borderDash: [6, 4], borderWidth: 1.5, pointRadius: 0, fill: false });
            });

            if (chart) { chart.destroy(); }
            chart = new Chart(canvas, {
                type: 'line',
                data: { labels: labels, datasets: datasets },
                options: {
                    animation: false, maintainAspectRatio: false, interaction: { mode: 'index', intersect: false },
                    plugins: {
                        legend: { labels: { color: text, usePointStyle: true, pointStyleWidth: 9, boxHeight: 7, filter: function (i) { return i.text !== 'Min' && i.text !== 'Maks'; } } },
                        tooltip: { callbacks: { label: function (c) { return c.dataset.label + ': ' + num.format(c.parsed.y) + ' ' + unit; } } },
                    },
                    scales: {
                        x: { ticks: { color: text, maxTicksLimit: 8, maxRotation: 0 }, grid: { display: false } },
                        y: { ticks: { color: text, callback: function (v) { return num.format(v); } }, grid: { color: grid }, title: { display: true, text: unit, color: text } },
                    },
                },
            });
            note.textContent = pts.length === 0 ? 'Bu aralıkta ölçüm yok.' : (raw ? 'Ham ölçümler' : 'Saatlik ortalama (gölge: min–maks)') + ' · ' + pts.length + ' nokta';
        }

        function load() {
            note.textContent = 'Yükleniyor…';
            var url = '/Assets/' + canvas.getAttribute('data-asset') + '/Series?code=' + encodeURIComponent(canvas.getAttribute('data-sensor')) + '&range=' + range;
            fetch(url, { headers: { 'X-Requested-With': 'fetch' }, credentials: 'same-origin' })
                .then(function (r) { if (!r.ok) { throw new Error(r.status); } return r.json(); })
                .then(render)
                .catch(function () { note.textContent = 'Grafik verisi alınamadı. Sayfayı yenileyin.'; });
        }

        card.querySelectorAll('.range-btn').forEach(function (btn) {
            btn.addEventListener('click', function () {
                card.querySelectorAll('.range-btn').forEach(function (b) { b.classList.remove('active'); });
                btn.classList.add('active');
                range = btn.getAttribute('data-range');
                load();
            });
        });
        window.addEventListener('ap-theme-changed', function () { if (chart) { load(); } });
        load();
    });
})();
