/* Minimal offline renderer for the .dc.html design sources in ./source (holes, sc-for, sc-if, dc-import). */
(function () {
  var SRC = window.DC_SOURCES || {};
  var cache = {};
  var helmetDone = {};
  function DCLogic(props) { this.props = props || {}; this.state = {}; }
  DCLogic.prototype.setState = function (p) { Object.assign(this.state, p); };
  DCLogic.prototype.forceUpdate = function () {};
  window.DCLogic = DCLogic;

  function prep(html) {
    return html.replace(/<sc-(for|if)\b/g, '<template data-sc="$1"').replace(/<\/sc-(for|if)>/g, '</template>');
  }
  function compile(name) {
    if (cache[name]) return cache[name];
    if (!SRC[name]) throw new Error('Missing design source: ' + name);
    var doc = new DOMParser().parseFromString(prep(SRC[name]), 'text/html');
    var tpl = doc.querySelector('x-dc');
    var helmet = tpl.querySelector('helmet');
    if (helmet) {
      if (!helmetDone[name]) {
        Array.prototype.slice.call(helmet.children).forEach(function (el) { document.head.appendChild(document.importNode(el, true)); });
        helmetDone[name] = true;
      }
      helmet.parentNode.removeChild(helmet);
    }
    var script = doc.querySelector('script[data-dc-script]');
    var Comp = new Function('DCLogic', script.textContent + '\nreturn Component;')(DCLogic);
    cache[name] = { tpl: tpl, Comp: Comp };
    return cache[name];
  }
  function lookup(path, scope) {
    path = path.trim();
    if (/^-?\d+(\.\d+)?$/.test(path)) return Number(path);
    if (path === 'true') return true;
    if (path === 'false') return false;
    if (path === 'null') return null;
    if (/^'.*'$|^".*"$/.test(path)) return path.slice(1, -1);
    var v = scope;
    var parts = path.split('.');
    for (var i = 0; i < parts.length; i++) { if (v == null) return undefined; v = v[parts[i]]; }
    return v;
  }
  var whole = /^\s*\{\{([^}]+)\}\}\s*$/;
  function raw(str, scope) {
    var m = whole.exec(str);
    if (m) return lookup(m[1], scope);
    return str.replace(/\{\{([^}]+)\}\}/g, function (_, p) { var v = lookup(p, scope); return v == null ? '' : String(v); });
  }
  function camel(s) { return s.replace(/-([a-z])/g, function (_, c) { return c.toUpperCase(); }); }
  function renderChildren(parentNode, scope, out) {
    var kids = parentNode.childNodes;
    for (var i = 0; i < kids.length; i++) renderNode(kids[i], scope, out);
  }
  function renderNode(node, scope, out) {
    if (node.nodeType === 3) { out.appendChild(document.createTextNode(raw(node.nodeValue, scope))); return; }
    if (node.nodeType !== 1) return;
    var tag = node.tagName.toLowerCase();
    if (tag === 'template' && node.getAttribute('data-sc') === 'for') {
      var list = raw(node.getAttribute('list') || '', scope) || [];
      var as = node.getAttribute('as') || 'item';
      for (var i = 0; i < list.length; i++) {
        var s = Object.create(scope); s[as] = list[i]; s.$index = i;
        renderChildren(node.content, s, out);
      }
      return;
    }
    if (tag === 'template' && node.getAttribute('data-sc') === 'if') {
      if (raw(node.getAttribute('value') || '', scope)) renderChildren(node.content, scope, out);
      return;
    }
    if (tag === 'dc-import') {
      var props = {};
      Array.prototype.slice.call(node.attributes).forEach(function (a) {
        if (a.name === 'name' || a.name.indexOf('hint-') === 0) return;
        props[camel(a.name)] = raw(a.value, scope);
      });
      out.appendChild(mount(node.getAttribute('name'), props));
      return;
    }
    var el = document.importNode(node, false);
    Array.prototype.slice.call(node.attributes).forEach(function (a) {
      var v = raw(a.value, scope);
      if (/^on[A-Z]/.test(a.name) || (/^on/.test(a.name) && typeof v === 'function')) { el.removeAttribute(a.name); return; }
      if (v === false || v == null) { el.removeAttribute(a.name); return; }
      if (v === true) { el.setAttribute(a.name, ''); return; }
      var text = String(v);
      if (a.name === 'href' && /\.dc\.html(#.*)?$/.test(text)) text = text.replace('.dc.html', '.html');
      el.setAttribute(a.name, text);
    });
    renderChildren(node, scope, el);
    out.appendChild(el);
  }
  function mount(name, props) {
    var c = compile(name);
    var inst = new c.Comp(props || {});
    var vals = inst.renderVals ? inst.renderVals() : {};
    var frag = document.createDocumentFragment();
    renderChildren(c.tpl, vals, frag);
    return frag;
  }
  window.DC = { mount: function (name, target) { (target || document.getElementById('dc-root')).appendChild(mount(name, {})); } };
})();
