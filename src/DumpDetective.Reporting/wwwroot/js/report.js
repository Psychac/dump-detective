document.addEventListener('DOMContentLoaded', function(){
  const toggle = document.getElementById('toggleTheme');
  toggle?.addEventListener('click', ()=>{
    const pressed = toggle.getAttribute('aria-pressed') === 'true';
    toggle.setAttribute('aria-pressed', String(!pressed));
    document.documentElement.classList.toggle('dark', !pressed);
  });

  // Simple demo charts — replace data with real payloads
  try{
    const genCtx = document.getElementById('genChart').getContext('2d');
    new Chart(genCtx, {
      type: 'doughnut',
      data: {labels:['Gen0','Gen1','Gen2'],datasets:[{data:[12,28,60],backgroundColor:['#66b3ff','#7ee9c6','#ffbf69']}]},
      options:{responsive:true,plugins:{legend:{position:'bottom'}}}
    });

    const trendCtx = document.getElementById('trendChart').getContext('2d');
    new Chart(trendCtx, {
      type:'line',
      data:{labels:['t-4','t-3','t-2','t-1','now'],datasets:[{label:'Objects',data:[9000,9400,10000,11000,12345],borderColor:'#0066cc',fill:false}]},
      options:{elements:{point:{radius:0}},plugins:{legend:{display:false}},scales:{x:{display:false}}}
    });
  }catch(e){console.warn('Charts init failed', e)}

  // Export PDF hint — the consumer should implement server-side rendering or client-side print
  const exportBtn = document.getElementById('exportPdf');
  exportBtn?.addEventListener('click', (ev)=>{
    ev.preventDefault();
    window.print();
  });
});
