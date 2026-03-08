using Microsoft.AspNetCore.Mvc;
using System.Threading.Tasks;
using Listenarr.Api.Services;
using Listenarr.Domain.Models;
using System.Collections.Generic;
using System;

namespace Listenarr.Api.Controllers
{
    [ApiController]
    [Route("api/v{version:apiVersion}/rootfolders")]
    [Tags("Root Folders")]
    public class RootFoldersController : ControllerBase
    {
        private readonly IRootFolderService _service;

        public RootFoldersController(IRootFolderService service)
        {
            _service = service;
        }

        /// <summary>
        /// List all configured root folders.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetAll()
        {
            var all = await _service.GetAllAsync();
            return Ok(all);
        }

        /// <summary>
        /// Get a single root folder by ID.
        /// </summary>
        /// <param name="id">Root folder ID.</param>
        [HttpGet("{id}")]
        public async Task<IActionResult> Get(int id)
        {
            var r = await _service.GetByIdAsync(id);
            if (r == null) return NotFound(new { message = "Root folder not found" });
            return Ok(r);
        }

        /// <summary>
        /// Create a new root folder.
        /// </summary>
        /// <param name="request">The root folder to create.</param>
        /// <returns>The newly created root folder.</returns>
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

        /// <summary>
        /// Update an existing root folder.
        /// </summary>
        /// <param name="id">Root folder ID.</param>
        /// <param name="request">Updated root folder data.</param>
        /// <param name="moveFiles">When true, physically move existing audiobook files to the new path.</param>
        /// <param name="deleteEmptySource">When true, delete the old root directory if it is empty after moving files.</param>
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

        /// <summary>
        /// Delete a root folder.
        /// </summary>
        /// <param name="id">Root folder ID to delete.</param>
        /// <param name="reassignTo">Optional ID of another root folder to reassign audiobooks to before deleting.</param>
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
