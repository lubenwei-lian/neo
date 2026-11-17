(function(){
  'use strict';
  // Keep real samples on a time axis. A missing interval is a gap, never invented traffic.
  var windowMs=64000,samples=[],views=new Map(),lastId=null,session=null,connected=false;
  var interval=1000,from=0,to=0,started=0,duration=0,sequence=0;
  var scale=0,scaleFrom=0,scaleTo=0,scaleStarted=0,scaleDuration=0,lowSince=0;
  var frame=0,lastPaint=-Infinity,nextPaint=0,dirty=true,drawCount=0,drawCost=0,background=false;
  var motion=matchMedia('(prefers-reduced-motion:reduce)');
  function active(){var page=document.querySelector('.page[data-page="conn"]');return page&&page.classList.contains('on');}
  function inBackground(){return background||document.hidden;}
  function fraction(now,start,length){return length?Math.max(0,Math.min(1,(now-start)/length)):1;}
  function right(now){return from+(to-from)*fraction(now,started,duration);}
  function range(now){var t=fraction(now,scaleStarted,scaleDuration);return scaleFrom+(scaleTo-scaleFrom)*(t*t*(3-2*t));}
  function rate(v){v=Number(v);return Number.isFinite(v)&&v>0?v:0;}
  function niceRange(v){if(v<=1024)return 1024;var base=Math.pow(1024,Math.max(0,Math.floor(Math.log(v)/Math.log(1024))));var n=v/base,p=Math.pow(10,Math.floor(Math.log10(n))-1);return Math.ceil(n/p)*p*base;}
  function chooseRange(now,at){
    var peak=0,edge=right(now)-windowMs;
    samples.forEach(function(s){if(s.at>=edge-interval)peak=Math.max(peak,s.down,s.up);});
    var target=niceRange(peak*1.2);
    if(!scaleTo){scale=scaleFrom=scaleTo=target;return;}
    if(target<scaleTo*.4){if(!lowSince)lowSince=at;}else lowSince=0;
    // Expand before the new point enters. Shrink only after the peak leaves the
    // visible window and five seconds of headroom, so ordinary samples never pump history.
    if((target>scaleTo&&peak>scaleTo*.92)||(lowSince&&at-lowSince>=5000)){
      scaleFrom=range(now);scaleTo=target;scaleStarted=now;scaleDuration=motion.matches?0:target>scaleFrom?420:1000;lowSince=0;
    }
  }
  function geometry(key,w,h){
    var left=8,rightEdge=w-12,bottom=h-12,height=Math.max(1,h-24),step=(rightEdge-left)/windowMs;
    var points=samples.map(function(s){return{x:rightEdge-(to-s.at)*step,y:bottom-s[key]/scale*height,gap:s.gap,at:s.at,value:s[key]};});
    var line=new Path2D(),area=new Path2D(),start=null,prev=null;
    points.forEach(function(p,i){
      var a=points[i-1],b=points[i+1];p.slope=0;
      if(!a||!b||p.gap||b.gap)return;
      var h1=p.x-a.x,h2=b.x-p.x,d1=(p.y-a.y)/h1,d2=(b.y-p.y)/h2;
      if(d1*d2>0){var w1=2*h2+h1,w2=h2+2*h1;p.slope=(w1+w2)/(w1/d1+w2/d2);}
    });
    // One real look-ahead sample gives each visible segment its final tangent.
    // Monotone interpolation passes through every sample without flat steps,
    // overshoot or rewriting already visible history when the next point arrives.
    points.forEach(function(p){
      if(!prev||p.gap){
        if(prev){area.lineTo(prev.x,bottom);area.lineTo(start.x,bottom);area.closePath();}
        line.moveTo(p.x,p.y);area.moveTo(p.x,p.y);start=p;
      }else{
        var third=(p.x-prev.x)/3,lo=Math.min(prev.y,p.y),hi=Math.max(prev.y,p.y);
        p.c1=Math.max(lo,Math.min(hi,prev.y+prev.slope*third));p.c2=Math.max(lo,Math.min(hi,p.y-p.slope*third));
        line.bezierCurveTo(prev.x+third,p.c1,p.x-third,p.c2,p.x,p.y);
        area.bezierCurveTo(prev.x+third,p.c1,p.x-third,p.c2,p.x,p.y);
      }
      prev=p;
    });
    if(prev){area.lineTo(prev.x,bottom);area.lineTo(start.x,bottom);area.closePath();}
    return{line:line,area:area,points:points,step:step,right:rightEdge,bottom:bottom};
  }
  function updateViews(){
    views.forEach(function(v,c){if(!c.isConnected){observer.unobserve(c);views.delete(c);}});
    document.querySelectorAll('.sparkCanvas').forEach(function(canvas){
      var w=canvas.clientWidth,h=canvas.clientHeight;if(w<1||h<1)return;
      var v=views.get(canvas),dpr=devicePixelRatio||1;
      if(!v||v.w!==w||v.h!==h||v.dpr!==dpr){
        canvas.width=Math.round(w*dpr);canvas.height=Math.round(h*dpr);
        var ctx=canvas.getContext('2d',{alpha:true});ctx.setTransform(dpr,0,0,dpr,0,0);
        var gradient=ctx.createLinearGradient(0,12,0,h-12);gradient.addColorStop(0,'rgba(90,169,255,.24)');gradient.addColorStop(.6,'rgba(90,169,255,.07)');gradient.addColorStop(1,'rgba(90,169,255,0)');
        var fade=ctx.createLinearGradient(0,0,w,0);fade.addColorStop(0,'rgba(255,255,255,0)');fade.addColorStop(.12,'white');
        v={canvas:canvas,ctx:ctx,w:w,h:h,dpr:dpr,gradient:gradient,fade:fade,font:getComputedStyle(canvas).fontFamily};views.set(canvas,v);observer.observe(canvas);
      }
      v.down=geometry('down',w,h);v.up=geometry('up',w,h);
      canvas.setAttribute('aria-label',connected?'实时下载与上传流量曲线，最近64秒':'未连接，等待记录流量');
    });
  }
  function marker(ctx,g,position,color,radius){
    var p=g.points;if(!p.length)return;var y=null;
    for(var i=p.length-1;i>=0;i--){
      if(p[i].at>position)continue;
      if(i===p.length-1)y=p[i].y;
      else if(!p[i+1].gap){var t=(position-p[i].at)/(p[i+1].at-p[i].at),u=1-t;y=u*u*u*p[i].y+3*u*u*t*p[i+1].c1+3*u*t*t*p[i+1].c2+t*t*t*p[i+1].y;}
      break;
    }
    if(y===null)return;ctx.fillStyle=color;ctx.beginPath();ctx.arc(g.right,y,radius,0,Math.PI*2);ctx.fill();
  }
  function render(now){
    frame=0;if(!active())return;
    // Follow display vsync up to 120 FPS; minimized frames come from the host's 24 Hz clock.
    var period=1000/120;
    if(!inBackground()&&now+.5<nextPaint){frame=requestAnimationFrame(render);return;}
    nextPaint=!nextPaint||now>nextPaint+period?now+period:nextPaint+period;
    var begin=performance.now(),nextScale=range(now),rescaling=nextScale!==scale;
    if(rescaling)scale=nextScale;
    if(dirty||rescaling){dirty=false;updateViews();}
    var position=right(now);
    views.forEach(function(v){
      if(!v.canvas.isConnected||!v.canvas.clientWidth)return;
      var ctx=v.ctx,w=v.w,h=v.h;ctx.clearRect(0,0,w,h);
      if(connected&&samples.length){
        var offset=(to-position)*v.down.step;
        ctx.save();ctx.beginPath();ctx.rect(0,4,v.down.right,h-8);ctx.clip();ctx.translate(offset,0);
        ctx.fillStyle=v.gradient;ctx.fill(v.down.area);
        ctx.lineCap='round';ctx.lineJoin='round';ctx.lineWidth=1.6;ctx.strokeStyle='#5aa9ff';ctx.stroke(v.down.line);
        ctx.lineWidth=1.4;ctx.strokeStyle='#43d17f';ctx.stroke(v.up.line);ctx.restore();
        ctx.save();ctx.globalCompositeOperation='destination-in';ctx.fillStyle=v.fade;ctx.fillRect(0,0,w,h);ctx.restore();
        marker(ctx,v.down,position,'#5aa9ff',2.4);marker(ctx,v.up,position,'#43d17f',2);
      }else{
        ctx.fillStyle='rgba(255,255,255,.28)';ctx.font='10.5px '+v.font;ctx.textAlign='center';ctx.fillText(connected?'等待流量采样':'连接后开始记录',w/2,h/2+4);
      }
      ctx.save();ctx.globalCompositeOperation='destination-over';ctx.strokeStyle='rgba(255,255,255,.045)';ctx.lineWidth=1;ctx.setLineDash([2,6]);
      [.25,.5,.75].forEach(function(r){var y=12+(h-24)*r;ctx.beginPath();ctx.moveTo(8,y);ctx.lineTo(w-12,y);ctx.stroke();});ctx.restore();
    });
    lastPaint=now;drawCount++;drawCost+=performance.now()-begin;
    if(!inBackground()&&connected&&(position<to||fraction(now,scaleStarted,scaleDuration)<1))frame=requestAnimationFrame(render);
  }
  function schedule(){
    dirty=true;if(!active())return;
    if(inBackground()){
      if(frame)cancelAnimationFrame(frame);frame=0;
    }else if(!frame)frame=requestAnimationFrame(render);
  }
  function reset(){samples=[];lastId=null;interval=1000;from=to=started=duration=sequence=0;scale=scaleFrom=scaleTo=scaleStarted=scaleDuration=lowSince=0;}
  window.neoTraffic={
    push:function(s){
      var stats=s.stats||{},nextSession=String(s.connectedAt||0)+':'+(stats.session||''),now=performance.now();
      var changed=session!==nextSession||connected!==!!s.connected;
      if(changed){reset();session=nextSession;}
      connected=!!s.connected;
      var visibilityChanged=background!==!!s.background;
      background=!!s.background;
      if(visibilityChanged){if(frame)cancelAnimationFrame(frame);frame=0;nextPaint=0;}
      var incoming=Array.isArray(stats.history)?stats.history.concat([stats]):[stats],added=0;
      incoming.forEach(function(point){
      var at=Number(point.sampledAt),prev=samples[samples.length-1];
      if(connected&&point.id!=null&&point.id!==lastId&&Number.isFinite(at)&&at>0&&(!prev||at>prev.at)){
        var gap=!!prev&&at-prev.at>Math.max(5000,interval*3);
        if(prev&&!gap)interval=Math.max(300,Math.min(2000,interval*.75+(at-prev.at)*.25));
        var current=right(now);lastId=point.id;added++;
        samples.push({at:at,down:rate(point.downBps),up:rate(point.upBps),gap:gap});
        // Keep one off-screen point for clipping; bound memory even while hidden.
        while(samples.length>2&&samples[1].at<at-windowMs-4000)samples.shift();
        if(samples.length>256)samples.splice(0,samples.length-256);
        // Draw one sample behind the numbers so the next tangent is known.
        // Both numbers still show the newest raw reading without a UI delay.
        to=prev&&!gap?prev.at:at;from=!prev||gap||!active()?to:current;started=now;duration=from===to||motion.matches?0:interval;
        sequence++;chooseRange(now,at);
      }
      });
      // Restore all real background samples at once, without replaying a minute of animation.
      if(added>1){from=to;duration=0;scale=scaleFrom=scaleTo;scaleDuration=0;}
      if(added||changed||visibilityChanged||!views.size)schedule();
    },
    draw:schedule,
    tick:function(){if(inBackground()&&active())render(performance.now());},
    inspect:function(){var v=Array.from(views.values())[0];return{
      samples:samples.map(function(s){return Object.assign({},s);}),down:samples.map(function(s){return s.down;}),up:samples.map(function(s){return s.up;}),
      connected:connected,background:inBackground(),targetFps:inBackground()?24:120,interval:interval,frame:!!frame,sequence:sequence,windowMs:windowMs,scale:scale,scaleTarget:scaleTo,
      scroll:{from:from,to:to,current:right(performance.now()),started:started,duration:duration},
      performance:{draws:drawCount,meanMs:drawCount?drawCost/drawCount:0},
      rendered:v?{width:v.w,height:v.h,dpr:v.dpr,scale:scale,down:v.down.points,up:v.up.points}:null};}
  };
  var observer=new ResizeObserver(schedule);observer.observe(document.documentElement);
  document.addEventListener('visibilitychange',function(){if(frame)cancelAnimationFrame(frame);frame=0;nextPaint=0;schedule();});
  motion.addEventListener('change',function(){if(motion.matches){from=to;duration=0;scaleFrom=scaleTo;scaleDuration=0;}schedule();});
})();
