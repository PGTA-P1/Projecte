using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Projecte1_WPF_.Classes;

namespace Projecte1_WPF_.ReadData
{
    /// Interfaz genérica para estandarizar la lectura de archivos de navegación/espacio aéreo.
    /// Facilita la incorporación de lectores para otros formatos (como .are) en el futuro.
    public interface IFileReader<T>
    {
        /// Parsea y procesa un archivo físico desde una ruta en disco.
        /// <returns>Objeto estructurado con los datos del archivo.</returns>
        T Read(string filePath);
    }
}