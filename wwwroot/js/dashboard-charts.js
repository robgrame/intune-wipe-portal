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

    _destroy: function (canvasId) {
        if (this._instances[canvasId]) {
            this._instances[canvasId].destroy();
            delete this._instances[canvasId];
        }
    }
};
