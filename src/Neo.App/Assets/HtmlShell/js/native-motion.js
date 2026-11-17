(function(){
  'use strict';
  window.nativeConnectionFeedback=function(){
    if(document.hidden||matchMedia('(prefers-reduced-motion:reduce)').matches)return;
    document.querySelectorAll('.ringButton').forEach(function(button){
      if(!button.offsetWidth)return;
      button.querySelectorAll('.native-connect-ripple').forEach(function(e){e.remove();});
      var ripple=document.createElement('span');ripple.className='native-connect-ripple';ripple.setAttribute('aria-hidden','true');button.appendChild(ripple);
      var animation=ripple.animate([{transform:'scale(.84)',opacity:0},{transform:'scale(.96)',opacity:.52,offset:.24},{transform:'scale(1.09)',opacity:0}],{duration:620,easing:'cubic-bezier(.22,1,.36,1)'});
      animation.onfinish=animation.oncancel=function(){ripple.remove();};
    });
  };
  window.nativeDialogMotion=function(dialog){
    var close=dialog.close.bind(dialog),closing=false;
    dialog.close=function(value){
      if(closing||!dialog.open)return;
      if(document.hidden||matchMedia('(prefers-reduced-motion:reduce)').matches){close(value);return;}
      closing=true;dialog.inert=true;
      var opacity=getComputedStyle(dialog).opacity,transform=getComputedStyle(dialog).transform;
      dialog.getAnimations().forEach(function(a){a.cancel();});
      var a=dialog.animate([{opacity:opacity,transform:transform},{opacity:0,transform:'translateY(7px) scale(.98)'}],{duration:200,easing:'cubic-bezier(.4,0,1,1)',fill:'forwards'});
      var done=function(){close(value);};a.onfinish=done;
    };
    dialog.addEventListener('cancel',function(e){e.preventDefault();dialog.close();});
  };
  window.nativeCloseMenu=function(menu){
    menu.style.pointerEvents='none';
    if(document.hidden||matchMedia('(prefers-reduced-motion:reduce)').matches){menu.remove();return;}
    var opacity=getComputedStyle(menu).opacity,transform=getComputedStyle(menu).transform;
    menu.getAnimations().forEach(function(a){a.cancel();});
    var a=menu.animate([{opacity:opacity,transform:transform},{opacity:0,transform:'translateY(-4px)'}],{duration:180,easing:'ease-in'});
    a.onfinish=a.oncancel=function(){menu.remove();};
  };
})();
