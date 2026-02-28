using Microsoft.AspNetCore.Mvc;
using System.Threading.Tasks;
using Listenarr.Api.Services;
using Listenarr.Domain.Models;
using System.Collections.Generic;
using System;

namespace Listenarr.Api.Controllers
{
    [ApiController]
    [Route("api/rootfolders")]
    public class RootFoldersController : ControllerBase
    {
        private readonly IRootFolderService _service;

        public RootFoldersController(IRootFolderService service)
        {
            _service = service;
        }

        [HttpGet]
        public async Task<IActionResult> GetAll()
        {
            var all = await _service.GetAllAsync();
            return Ok(all);
        }

        [HttpGet("{id}")]
        public async Task<IActionResult> Get(int id)
        {
            var r = await _service.GetByIdAsync(id);
            if (r == null) return NotFound(new { message = "Root folder not found" });
            return Ok(r);
        }

        [HttpPost]
        public async Task<IActionResult> Create([FromBody] RootFolder request)
        {
            try
            {
                var created = await _service.CreateAsync(request);
                return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
            }
            catch (ArgumentException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                return StatusCode(500, new { message = "Failed to create root folder", error = ex.Message });
            }
        }

        [HttpPut("{id}")]
        public async Task<IActionResult> Update(int id, [FromBody] RootFolder request, [FromQuery] bool moveFiles = false, [FromQuery] bool deleteEmptySource = true)
        {
            if (id != request.Id) return BadRequest(new { message = "Id mismatch" });
            try
            {
                var updated = await _service.UpdateAsync(request, moveFiles, deleteEmptySource);
                return Ok(updated);
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(new { message = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                return StatusCode(500, new { message = "Failed to update root folder", error = ex.Message });
            }
        }

        [HttpDelete("{id}")]
        public async Task<IActionResult> Delete(int id, [FromQuery] int? reassignTo = null)
        {
            try
            {
                await _service.DeleteAsync(id, reassignTo);
                return Ok(new { message = "Deleted" });
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(new { message = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException) {
                return StatusCode(500, new { message = "Failed to delete root folder", error = ex.Message });
            }
        }
    }
}
