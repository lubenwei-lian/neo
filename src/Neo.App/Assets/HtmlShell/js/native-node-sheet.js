  // Shared latency ordering, independent of the filters on the nodes page.
  // Keep cards alive while results arrive so focus and scrolling survive sorting.
  var nodeSheetCards=new Map(),nodeSheetSignature='',nodeSheetOrder='';
  function buildNodeSheet(){syncNodeSheet(true);}
  function syncNodeSheet(force){
    var list=$('#sheetList'),visible=nodeSheetIsVisible();
    if(!list||(!force&&!visible))return;
    var order=nodeOrder(true);
    var signature=JSON.stringify(order.map(function(i){var n=NODES[i];return [n.id||i,n.name,n.f,n.proto,n.real,i===CUR];}));
    if(signature===nodeSheetSignature)return;
    var keys=order.map(function(i){return NODES[i].id||'index:'+i;}),orderKey=JSON.stringify(keys);
    var moving=visible&&orderKey!==nodeSheetOrder&&!matchMedia('(prefers-reduced-motion:reduce)').matches;
    var positions=new Map(),viewport=moving?list.getBoundingClientRect():null;
    if(moving)nodeSheetCards.forEach(function(card){if(card.isConnected)positions.set(card,card.getBoundingClientRect());});
    var scrollTop=list.scrollTop,focused=list.contains(document.activeElement)?document.activeElement:null,keep=new Set(keys);
    nodeSheetCards.forEach(function(card,key){if(!keep.has(key)){card.remove();nodeSheetCards.delete(key);}});
    order.forEach(function(i,position){
      var n=NODES[i],key=keys[position],card=nodeSheetCards.get(key);
      if(!card){
        card=document.createElement('button');card.type='button';card.className='node-card sheet-node';
        card.innerHTML='<span class="node-name"><span class="flag"></span><b></b></span><span class="node-bottom"><span class="node-proto"></span></span>';
        card.__lat=latNode(n.real);card.__real=n.real;card.querySelector('.node-bottom').appendChild(card.__lat);
        card.addEventListener('click',function(e){
          e.stopPropagation();var index=+card.dataset.node,node=NODES[index];if(!node)return;
          if(NATIVE){
            card.disabled=true;
            host('node.select',{key:node.id}).then(function(){closeMenu();closeNodeSheet();}).catch(function(){}).finally(function(){card.disabled=false;});
            return;
          }
          CUR=index;paintCurrent();nSelect(index);syncNodeSheet(true);closeNodeSheet();toast('已切换到 '+node.name);
        });
        nodeSheetCards.set(key,card);
      }
      card.dataset.node=String(i);card.dataset.key=key;card.title=n.name;
      card.setAttribute('aria-label','切换到 '+n.name);card.setAttribute('aria-pressed',String(i===CUR));card.classList.toggle('on',i===CUR);
      if(card.__name!==n.name){card.querySelector('.node-name b').textContent=n.name;card.__name=n.name;}
      if(card.__flag!==n.f){card.querySelector('.flag').innerHTML=FLAGS[n.f]||'';card.__flag=n.f;}
      if(card.__proto!==n.proto){card.querySelector('.node-proto').textContent=n.proto;card.__proto=n.proto;}
      if(card.__real!==n.real){setLat(card.__lat,n.real);card.__real=n.real;}
      var before=list.children[position]||null;
      if(card!==before){if(list.moveBefore&&card.parentNode===list)list.moveBefore(card,before);else list.insertBefore(card,before);}
    });
    if(!order.length){
      if(!list.querySelector('.empty-state'))list.innerHTML='<div class="empty-state">没有可用的节点记录</div>';
    }else{var empty=list.querySelector('.empty-state');if(empty)empty.remove();}
    if(focused&&focused.isConnected&&document.activeElement!==focused)focused.focus({preventScroll:true});
    list.scrollTop=scrollTop;
    if(moving)nodeSheetCards.forEach(function(card){
      var before=positions.get(card);if(!before)return;
      if(card.__sortAnimation){card.__sortAnimation.cancel();card.__sortAnimation=null;}
      var after=card.getBoundingClientRect(),dx=before.left-after.left,dy=before.top-after.top;
      if((dx||dy)&&((before.bottom>=viewport.top&&before.top<=viewport.bottom)||(after.bottom>=viewport.top&&after.top<=viewport.bottom))){
        var animation=card.animate([{transform:'translate('+dx+'px,'+dy+'px)'},{transform:'none'}],{duration:220,easing:'cubic-bezier(.22,1,.36,1)'});
        card.__sortAnimation=animation;animation.onfinish=function(){if(card.__sortAnimation===animation)card.__sortAnimation=null;};
      }
    });
    nodeSheetSignature=signature;nodeSheetOrder=orderKey;
  }
