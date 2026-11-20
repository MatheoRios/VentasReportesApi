// Construye la app web: une los módulos de js/ en app.js y genera app.css con Tailwind.
//   En el proyecto:  cd web-fuente  y  node construir.js   (escribe en ../wwwroot/app)
//   No edite wwwroot/app/app.js a mano: se reemplaza al construir. Edite los archivos de web-fuente/js.
const fs = require('fs'), path = require('path'), { execSync } = require('child_process');
const aqui = __dirname, existe = p => fs.existsSync(path.join(aqui, p));
const dirJs = existe('js') ? path.join(aqui, 'js') : path.join(aqui, 'src', 'js');
const salida = existe('../wwwroot/app') ? path.join(aqui, '..', 'wwwroot', 'app') : path.join(aqui, 'web');
const css = existe('input.css') ? 'input.css' : 'src/input.css';
const partes = fs.readdirSync(dirJs).filter(f => f.endsWith('.js')).sort();
fs.writeFileSync(path.join(salida, 'app.js'), partes.map(f => fs.readFileSync(path.join(dirJs, f), 'utf8')).join(''));
console.log('app.js =', partes.join(' + '));
execSync(`npx tailwindcss@3.4.17 -c tailwind.config.js -i ${css} -o "${path.join(salida, 'app.css')}" --minify`, { cwd: aqui, stdio: 'inherit' });
console.log('Listo. Recuerde subir la versión (?v=) en index.html y sw.js si cambió app.js o app.css.');
