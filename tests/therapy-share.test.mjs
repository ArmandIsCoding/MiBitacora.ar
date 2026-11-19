import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { runInNewContext } from 'node:vm';

const source = (await readFile(new URL('../wwwroot/therapy-share.js', import.meta.url), 'utf8')).replace(/^export /gm, '');
function page(fragment = 'A'.repeat(43)) {
    const details = [{open:false,isConnected:true},{open:true,isConnected:true}], frames = [], listeners = new Map(), observations = [];
    const location = { href: `https://example.test/compartido?vista=informe#${fragment}`, hash: '#' + fragment, pathname: '/compartido', search: '?vista=informe' };
    const history = { state: { internal: true }, replaceState: (_state, _title, url) => {
        const parsed = new URL(url, location.href); Object.assign(location, { href:parsed.href,hash:parsed.hash,pathname:parsed.pathname,search:parsed.search });
    }};
    const window = { addEventListener: (name, callback) => listeners.set(name, callback), print: () => observations.push({url:location.href,open:details.map(d=>d.open)}) };
    const context = {location,history,window,navigator:{clipboard:{writeText:async value=>observations.push(value)}},requestAnimationFrame:callback=>frames.push(callback),document:{getElementById:()=>null},matchMedia:()=>({matches:false})};
    runInNewContext(source + '\nglobalThis.api={readToken,printReport,copyLink,openDialog,closeDialog};',context);
    return { api:context.api,location,details,listeners,observations,context,flush:()=>{while(frames.length)frames.shift()();} };
}
test('the capability is read from the fragment and rejects malformed or encoded tokens',()=>{
    const p=page(); assert.equal(p.api.readToken(),'A'.repeat(43));
    for(const fragment of ['', 'A'.repeat(42), 'A'.repeat(44), 'A'.repeat(43)+'\n', '%41'.repeat(43), 'token?extra']) assert.equal(page(fragment).api.readToken(),null);
    const plain=page(''); plain.location.search='?token='+'A'.repeat(43); assert.equal(plain.api.readToken(),null);
});
test('printing expands supporting evidence, omits the capability from the print URL and restores the page',()=>{
    const p=page(); const original=p.location.href;
    p.api.printReport({querySelectorAll:()=>p.details}); assert.deepEqual(p.details.map(d=>d.open),[true,true]); p.flush();
    assert.equal(p.observations[0].url,'https://example.test/compartido?vista=informe');
    assert.deepEqual(Array.from(p.observations[0].open),[true,true]); assert.equal(p.location.href,original);
    assert.deepEqual(p.details.map(d=>d.open),[false,true]); p.listeners.get('afterprint')(); assert.equal(p.location.href,original);
});
test('copying a share link uses the selected input and offers manual copy if clipboard permission is denied',async()=>{
    const p=page(), input={value:p.location.href,focus(){this.focused=true;},select(){this.selected=true;}};
    assert.equal(await p.api.copyLink(input),true); assert.equal(p.observations[0],input.value);
    p.context.navigator.clipboard.writeText=async()=>{throw new Error('Denied');};
    assert.equal(await p.api.copyLink(input),false); assert.equal(input.focused,true); assert.equal(input.selected,true);
    assert.equal(await p.api.copyLink(null),false);
});
test('record dialogs can be opened and closed repeatedly without duplicating the native dialog',()=>{
    const p=page(), dialog={open:false,opens:0,closes:0,showModal(){this.open=true;this.opens++;},close(){this.open=false;this.closes++;}};
    p.api.openDialog(dialog);p.api.openDialog(dialog);p.api.closeDialog(dialog);p.api.closeDialog(dialog);
    assert.equal(dialog.opens,1);assert.equal(dialog.closes,1);
});
test('printing a private preview excludes the owner controls and other share links',()=>{
    const p=page();
    const element=()=>{const names=new Set();return{children:[],classList:{contains:n=>names.has(n),add:n=>names.add(n),remove:n=>names.delete(n)}};};
    const body=element(),main=element(),privateHeader=element(),privateLinks=element(),host=element();
    body.children=[privateHeader,main];main.parentElement=body;main.children=[host,privateLinks];host.parentElement=main;host.querySelectorAll=()=>p.details;
    p.context.document.body=body;
    p.context.window.print=()=>{assert.equal(privateHeader.classList.contains('therapy-print-hidden'),true);assert.equal(privateLinks.classList.contains('therapy-print-hidden'),true);assert.equal(main.classList.contains('therapy-print-ancestor'),true);};
    p.api.printReport(host);p.flush();
    assert.equal(privateHeader.classList.contains('therapy-print-hidden'),false);assert.equal(privateLinks.classList.contains('therapy-print-hidden'),false);assert.equal(main.classList.contains('therapy-print-ancestor'),false);
});
