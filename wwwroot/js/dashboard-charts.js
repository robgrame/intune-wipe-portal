// Dashboard chart interop
window.dashboardCharts = {
    _instances: {},

    renderDoughnut: function (canvasId, labels, data, colors) {
        this._destroy(canvasId);
        const ctx = document.getElementById(canvasId);
        if (!ctx) return;
        this._instances[canvasId] = new Chart(ctx, {
            type: 'doughnut',
            data: {
                labels: labels,
                datasets: [{
                    data: data,
                    backgroundColor: colors,
                    borderWidth: 1
                }]
            },
            options: {
                responsive: true,
                maintainAspectRatio: false,
                plugins: {
                    legend: { position: 'bottom', labels: { boxWidth: 12, font: { size: 11 } } }
                },
                cutout: '55%'
            }
        });
    },

    renderBar: function (canvasId, labels, data, colors) {
        this._destroy(canvasId);
        const ctx = document.getElementById(canvasId);
        if (!ctx) return;
        this._instances[canvasId] = new Chart(ctx, {
            type: 'bar',
            data: {
                labels: labels,
                datasets: [{
                    data: data,
                    backgroundColor: colors,
                    borderWidth: 0,
                    borderRadius: 4
                }]
            },
            options: {
                responsive: true,
                maintainAspectRatio: false,
                indexAxis: 'y',
                plugins: {
                    legend: { display: false }
                },
                scales: {
                    x: { beginAtZero: true, grid: { display: false } },
                    y: { grid: { display: false } }
                }
            }
        });
    },

    // Multi-series line chart for time-series metrics (throughput, latenza).
    renderLine: function (canvasId, labels, datasets, opts) {
        this._destroy(canvasId);
        const ctx = document.getElementById(canvasId);
        if (!ctx) return;
        opts = opts || {};
        const series = datasets.map(function (d) {
            return {
                label: d.label,
                data: d.data,
                borderColor: d.color,
                backgroundColor: d.fill ? (d.fillColor || d.color) : 'transparent',
                fill: !!d.fill,
                tension: 0.3,
                borderWidth: 2,
                pointRadius: 0,
                pointHoverRadius: 4,
                yAxisID: d.axis || 'y'
            };
        });
        const scales = {
            x: { grid: { display: false }, ticks: { maxTicksLimit: 12, font: { size: 10 } } },
            y: { beginAtZero: true, grid: { color: 'rgba(0,0,0,.05)' }, title: { display: !!opts.yTitle, text: opts.yTitle } }
        };
        if (opts.y1Title) {
            scales.y1 = {
                beginAtZero: true, position: 'right',
                grid: { display: false },
                title: { display: true, text: opts.y1Title }
            };
        }
        this._instances[canvasId] = new Chart(ctx, {
            type: 'line',
            data: { labels: labels, datasets: series },
            options: {
                responsive: true,
                maintainAspectRatio: false,
                interaction: { mode: 'index', intersect: false },
                plugins: {
                    legend: { position: 'bottom', labels: { boxWidth: 12, font: { size: 11 } } }
                },
                scales: scales
            }
        });
    },

    // Vertical bar chart, optionally stacked, with named datasets.
    renderStackedBar: function (canvasId, labels, datasets, stacked) {
        this._destroy(canvasId);
        const ctx = document.getElementById(canvasId);
        if (!ctx) return;
        const series = datasets.map(function (d) {
            return { label: d.label, data: d.data, backgroundColor: d.color, borderRadius: 3 };
        });
        this._instances[canvasId] = new Chart(ctx, {
            type: 'bar',
            data: { labels: labels, datasets: series },
            options: {
                responsive: true,
                maintainAspectRatio: false,
                plugins: { legend: { position: 'bottom', labels: { boxWidth: 12, font: { size: 11 } } } },
                scales: {
                    x: { stacked: !!stacked, grid: { display: false }, ticks: { font: { size: 10 } } },
                    y: { stacked: !!stacked, beginAtZero: true, grid: { color: 'rgba(0,0,0,.05)' } }
                }
            }
        });
    },

    _destroy: function (canvasId) {
        if (this._instances[canvasId]) {
            this._instances[canvasId].destroy();
            delete this._instances[canvasId];
        }
    }
};
