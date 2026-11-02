// Runs d3-hierarchy's squarifyRatio verbatim on fixed datasets and prints JSON reference rectangles.
import {readFileSync, writeFileSync} from 'fs';
function treemapDice(parent, x0, y0, x1, y1) { var nodes = parent.children, node, i = -1, n = nodes.length, k = parent.value && (x1 - x0) / parent.value; while (++i < n) { node = nodes[i], node.y0 = y0, node.y1 = y1; node.x0 = x0, node.x1 = x0 += node.value * k; } }
function treemapSlice(parent, x0, y0, x1, y1) { var nodes = parent.children, node, i = -1, n = nodes.length, k = parent.value && (y1 - y0) / parent.value; while (++i < n) { node = nodes[i], node.x0 = x0, node.x1 = x1; node.y0 = y0, node.y1 = y0 += node.value * k; } }
function squarifyRatio(ratio, parent, x0, y0, x1, y1) {
  var rows = [], nodes = parent.children, row, nodeValue, i0 = 0, i1 = 0, n = nodes.length, dx, dy, value = parent.value, sumValue, minValue, maxValue, newRatio, minRatio, alpha, beta;
  while (i0 < n) {
    dx = x1 - x0, dy = y1 - y0;
    do sumValue = nodes[i1++].value; while (!sumValue && i1 < n);
    minValue = maxValue = sumValue;
    alpha = Math.max(dy / dx, dx / dy) / (value * ratio);
    beta = sumValue * sumValue * alpha;
    minRatio = Math.max(maxValue / beta, beta / minValue);
    for (; i1 < n; ++i1) {
      sumValue += nodeValue = nodes[i1].value;
      if (nodeValue < minValue) minValue = nodeValue;
      if (nodeValue > maxValue) maxValue = nodeValue;
      beta = sumValue * sumValue * alpha;
      newRatio = Math.max(maxValue / beta, beta / minValue);
      if (newRatio > minRatio) { sumValue -= nodeValue; break; }
      minRatio = newRatio;
    }
    rows.push(row = {value: sumValue, dice: dx < dy, children: nodes.slice(i0, i1)});
    if (row.dice) treemapDice(row, x0, y0, x1, value ? y0 += dy * sumValue / value : y1);
    else treemapSlice(row, x0, y0, value ? x0 += dx * sumValue / value : x1, y1);
    value -= sumValue, i0 = i1;
  }
  return rows;
}
function run(values, w, h, ratio) {
  const nodes = values.map(v => ({value: v}));
  const parent = {value: values.reduce((a,b)=>a+b,0), children: nodes};
  const rows = squarifyRatio(ratio, parent, 0, 0, w, h);
  return {values, w, h, ratio, rects: nodes.map(n => [n.x0, n.y0, n.x1 - n.x0, n.y1 - n.y0]), rows: rows.map(r => [r.children.length, r.dice ? 1 : 0])};
}
// deterministic pseudo random
let seed = 12345; const rnd = () => (seed = (seed * 1103515245 + 12345) % 2147483648) / 2147483648;
const cases = [];
cases.push(run([6,6,4,3,2,2,1], 6, 4, 1));           // the Bruls paper example
cases.push(run([6,6,4,3,2,2,1], 6, 4, (1+Math.sqrt(5))/2));
cases.push(run([100], 300, 200, 1));
cases.push(run([70,30], 300, 200, 1));
cases.push(run([50,50,50,50], 400, 100, 1));
cases.push(run([1000,1,1,1,1,1,1,1,1,1,1], 500, 300, 1));
cases.push(run([5,4,3,0,0], 200, 100, 1));
{ const v = []; for (let i=0;i<100;i++) v.push(Math.floor(rnd()*1e6)+1); v.sort((a,b)=>b-a); cases.push(run(v, 1024, 768, 1)); }
{ const v = []; for (let i=0;i<1000;i++) v.push(Math.pow(10, rnd()*9)); v.sort((a,b)=>b-a); cases.push(run(v, 1920, 1080, 1.5)); }
writeFileSync('d3_reference.json', JSON.stringify(cases));
console.log('cases', cases.length, 'first rects', JSON.stringify(cases[0].rects));
