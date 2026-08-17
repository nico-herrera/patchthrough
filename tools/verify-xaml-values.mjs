#!/usr/bin/env node
// Checks that every design token a XAML attribute consumes has the type the
// property needs.
//
// `CornerRadius="7"` works in XAML because a string goes through the
// property's type converter. `CornerRadius="{x:Static th:PT+M.RowRadius}"`
// does not: x:Static assigns the value directly, no converter runs, and a
// raw double is not a CornerRadius. The assignment compiles cleanly on every
// platform and throws XamlParseException when the BAML loads, which is to
// say on Windows only, at startup. The first CI render of the interface
// found exactly that.
//
// The mismatch cuts both ways. The second CI render found the reverse: a
// Separator's Height, a double, fed the Thickness Hairline token by a
// careless rewrite. So double-typed properties are checked too, and Width
// and Height are resolved against their element, because the same attribute
// needs a GridLength on a ColumnDefinition and a double everywhere else.
//
// It also refuses a markup extension embedded inside a longer attribute
// string, like Margin="0,{x:Static …},0,8". XAML treats a value that does
// not start with `{` as a plain string, so the "extension" reaches the type
// converter as literal text and fails the same way.

import { readFileSync, readdirSync, statSync } from 'node:fs';
import path from 'node:path';

const repoRoot = path.resolve(import.meta.dirname, '..');
const appRoot = path.join(repoRoot, 'windows/src/Patchthrough.App');
const themePath = path.join(appRoot, 'Theme/PT.cs');

// What each property needs when fed by x:Static. Width and Height mean
// GridLength on grid definitions, handled below.
const REQUIRED = {
  Margin: 'Thickness',
  Padding: 'Thickness',
  BorderThickness: 'Thickness',
  CornerRadius: 'CornerRadius',
  Width: 'double',
  Height: 'double',
  MinWidth: 'double',
  MinHeight: 'double',
  MaxWidth: 'double',
  MaxHeight: 'double',
  FontSize: 'double',
  StrokeThickness: 'double',
  LineHeight: 'double',
};
const GRID_ELEMENTS = new Set(['ColumnDefinition', 'RowDefinition']);

function walk(directory, extension, found = []) {
  for (const entry of readdirSync(directory)) {
    const full = path.join(directory, entry);
    if (entry === 'obj' || entry === 'bin') continue;
    if (statSync(full).isDirectory()) walk(full, extension, found);
    else if (entry.endsWith(extension)) found.push(full);
  }
  return found;
}

/** Token types per PT nested class, read from the theme source itself. */
function readTokenTypes() {
  const text = readFileSync(themePath, 'utf8');
  const types = new Map();
  let currentClass = null;
  for (const line of text.split('\n')) {
    const classDeclaration = /^\s*public static class (\w+)/.exec(line);
    if (classDeclaration) {
      currentClass = classDeclaration[1];
      continue;
    }
    if (!currentClass) continue;
    const constant = /^\s*public const (\w+) (\w+)\s*=/.exec(line);
    if (constant) types.set(`${currentClass}.${constant[2]}`, constant[1]);
    const field = /^\s*public static readonly ([\w.]+) (\w+)\s*=/.exec(line);
    if (field) types.set(`${currentClass}.${field[2]}`, field[1]);
  }
  return types;
}

const STATIC_VALUE = /^\{x:Static th:PT\+(\w+)\.(\w+)\}$/;

function main() {
  const types = readTokenTypes();
  const problems = [];
  const files = walk(appRoot, '.xaml');

  /** What `property` on `element` needs, or null when there is no rule. */
  const neededType = (element, property) => {
    if (GRID_ELEMENTS.has(element) && (property === 'Width' || property === 'Height')) {
      return 'GridLength';
    }
    return REQUIRED[property] ?? null;
  };

  const check = (file, element, property, value) => {
    const isStatic = STATIC_VALUE.exec(value);
    if (!isStatic) return;
    const needed = neededType(element, property);
    if (needed === null) return;
    const token = `${isStatic[1]}.${isStatic[2]}`;
    const actual = types.get(token);
    if (actual === undefined || actual === needed) return; // unknown: not ours to judge
    problems.push(
      `${file}: ${element} ${property} uses PT.${token}, which is ${actual}; `
      + `${property} needs ${needed}`);
  };

  for (const file of files) {
    const name = path.basename(file);
    const text = readFileSync(file, 'utf8');

    // A markup extension that is not the whole attribute value is a string.
    for (const match of text.matchAll(/(\w+)="([^"{][^"]*\{x:Static[^"]*)"/g)) {
      problems.push(
        `${name}: ${match[1]}="${match[2]}" embeds x:Static in a string, `
        + 'so the type converter receives it as literal text');
    }

    // Element-aware pass: every start tag, with its attributes, so Width on
    // a ColumnDefinition and Width on a Border get different rules.
    for (const tag of text.matchAll(/<((?:\w+:)?\w+)((?:[^>"]|"[^"]*")*?)\/?>/g)) {
      const element = tag[1].split(':').pop();
      const attributes = tag[2];
      if (element === 'Setter') {
        const property = /\bProperty="(\w+)"/.exec(attributes);
        const value = /\bValue="([^"]*)"/.exec(attributes);
        // A style's setter has no element context, so Width and Height fall
        // back to the double rule, which is what every current setter wants.
        if (property && value) check(name, 'Setter', property[1], value[1]);
        continue;
      }
      for (const attribute of attributes.matchAll(/([\w.]+)="([^"]*)"/g)) {
        const [, property, value] = attribute;
        if (property.includes('.')) continue; // attached properties: no rule
        check(name, element, property, value);
      }
    }
  }

  if (problems.length > 0) {
    console.error('XAML values whose token type cannot load:\n');
    for (const problem of problems.sort()) console.error(`  ${problem}`);
    console.error(
      '\nx:Static assigns without a type converter, so each of these throws '
      + 'XamlParseException when the BAML loads, on Windows only.');
    process.exit(1);
  }
  console.log(`verify-xaml-values: ${files.length} XAML files, every token type loads`);
}

main();
