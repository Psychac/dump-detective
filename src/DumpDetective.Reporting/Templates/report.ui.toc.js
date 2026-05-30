export function filterTocList(list, query) {
  let anyVisible = false;
  const items = Array.from(list.children).filter(function (child) { return child.tagName === 'LI'; });

  for (const item of items) {
    const link = item.firstElementChild;
    const nestedList = Array.from(item.children).find(function (child) { return child.tagName === 'OL'; }) || null;
    const selfText = link && link.textContent ? link.textContent.toLowerCase() : '';
    const selfMatch = !query || selfText.includes(query);
    const childMatch = nestedList ? filterTocList(nestedList, query) : false;
    const visible = selfMatch || childMatch;
    item.hidden = !visible;
    anyVisible = anyVisible || visible;
  }

  return anyVisible;
}